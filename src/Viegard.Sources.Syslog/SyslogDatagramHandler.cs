using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Viegard.Application.Net;
using Viegard.Application.Sources;
using Viegard.Domain;
using Viegard.Domain.Events;

namespace Viegard.Sources.Syslog;

/// <summary>Why a datagram was dropped (guardrails, D-0023).</summary>
public enum DatagramDropReason
{
    SourceNotAllowed,
    TooLarge,
    RateLimited,
}

/// <summary>Result of handling one datagram: either an observed item or a drop reason.</summary>
public sealed record DatagramResult
{
    public ObservedItem? Item { get; init; }

    public DatagramDropReason? DropReason { get; init; }
}

/// <summary>
/// The testable core of the syslog listener: enforces the source-IP
/// allowlist, size cap, and per-source rate cap, then wraps accepted
/// datagrams as observed items.  Transport (UDP socket) stays in
/// <see cref="SyslogUdpSource"/>.
/// </summary>
public sealed class SyslogDatagramHandler
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly SyslogSourceOptions _options;
    private readonly TimeProvider _time;
    private readonly CidrSet _allowed;
    private readonly ConcurrentDictionary<IPAddress, TokenBucket> _buckets = new();
    private readonly string _sourceId;

    private long _sequence;
    private long _droppedNotAllowed;
    private long _droppedTooLarge;
    private long _droppedRateLimited;

    public SyslogDatagramHandler(SyslogSourceOptions options, string sourceId, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        _options = options;
        _sourceId = sourceId;
        _time = timeProvider ?? TimeProvider.System;
        _allowed = new CidrSet(options.AllowedSources);
    }

    public long DroppedNotAllowed => Interlocked.Read(ref _droppedNotAllowed);

    public long DroppedTooLarge => Interlocked.Read(ref _droppedTooLarge);

    public long DroppedRateLimited => Interlocked.Read(ref _droppedRateLimited);

    public DatagramResult Handle(IPAddress peer, ReadOnlySpan<byte> datagram)
    {
        ArgumentNullException.ThrowIfNull(peer);

        if (!_allowed.Contains(peer))
        {
            Interlocked.Increment(ref _droppedNotAllowed);
            return new DatagramResult { DropReason = DatagramDropReason.SourceNotAllowed };
        }

        if (datagram.Length > _options.MaxDatagramBytes)
        {
            Interlocked.Increment(ref _droppedTooLarge);
            return new DatagramResult { DropReason = DatagramDropReason.TooLarge };
        }

        var now = _time.GetUtcNow();
        var bucket = _buckets.GetOrAdd(peer, _ => new TokenBucket(_options.MaxDatagramsPerSourcePerSecond, now));
        if (!bucket.TryTake(now))
        {
            Interlocked.Increment(ref _droppedRateLimited);
            return new DatagramResult { DropReason = DatagramDropReason.RateLimited };
        }

        // Replacement-character decoding: hostile bytes can never throw here.
        var raw = Encoding.UTF8.GetString(datagram);
        var sequence = Interlocked.Increment(ref _sequence);

        var dto = new SyslogDatagramDto
        {
            SchemaVersion = 1,
            PeerIp = peer.ToString(),
            ReceivedAt = now,
            Raw = raw,
        };

        return new DatagramResult
        {
            Item = new ObservedItem
            {
                Observation = new RawObservation
                {
                    Id = ViegardId.New(),
                    SourceId = _sourceId,
                    ObservedAt = now,
                    PayloadReference = $"syslog/{peer}/{now.UtcTicks}/{sequence}",
                },
                RawPayload = JsonSerializer.Serialize(dto, SerializerOptions),
            },
        };
    }

    /// <summary>Simple per-source token bucket: capacity and refill rate equal the configured per-second cap.</summary>
    private sealed class TokenBucket(int ratePerSecond, DateTimeOffset start)
    {
        private readonly Lock _lock = new();
        private double _tokens = ratePerSecond;
        private DateTimeOffset _lastRefill = start;

        public bool TryTake(DateTimeOffset now)
        {
            lock (_lock)
            {
                var elapsed = (now - _lastRefill).TotalSeconds;
                if (elapsed > 0)
                {
                    _tokens = Math.Min(ratePerSecond, _tokens + (elapsed * ratePerSecond));
                    _lastRefill = now;
                }

                if (_tokens >= 1)
                {
                    _tokens -= 1;
                    return true;
                }

                return false;
            }
        }
    }
}