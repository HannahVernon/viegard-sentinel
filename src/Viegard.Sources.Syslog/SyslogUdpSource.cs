using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Viegard.Application.Health;
using Viegard.Application.Sources;
using Viegard.Domain.Health;

namespace Viegard.Sources.Syslog;

/// <summary>
/// UDP syslog listener source (D-0023).  The socket loop stays thin; all
/// guardrail logic lives in <see cref="SyslogDatagramHandler"/>.
/// </summary>
public sealed class SyslogUdpSource(
    SyslogSourceOptions options,
    ILogger<SyslogUdpSource> logger) : IDataSource, IHealthContributor
{
    private readonly SyslogDatagramHandler _handler = new(options, $"syslog:udp-{options.Port}");

    private bool _listening;
    private string? _lastError;

    public string SourceId => $"syslog:udp-{options.Port}";

    public string SourceType => SyslogEventNormalizer.SyslogSourceType;

    public string ComponentId => SourceId;

    public Task<ComponentHealth> CheckAsync(CancellationToken cancellationToken = default)
    {
        var detail = _listening
            ? $"Listening on {options.ListenAddress}:{options.Port} (dropped: {_handler.DroppedNotAllowed} disallowed, {_handler.DroppedTooLarge} oversized, {_handler.DroppedRateLimited} rate-limited)."
            : _lastError ?? "Not listening yet.";

        return Task.FromResult(new ComponentHealth
        {
            ComponentId = ComponentId,
            Status = _listening ? HealthStatus.Healthy : HealthStatus.Unhealthy,
            Detail = detail,
            CheckedAt = DateTimeOffset.UtcNow,
        });
    }

    public async IAsyncEnumerable<ObservedItem> ObserveAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Parse(options.ListenAddress), options.Port));
        _listening = true;
        logger.LogInformation(
            "Syslog listener bound to {Address}:{Port} with {AllowedCount} allowed source(s).",
            options.ListenAddress, options.Port, options.AllowedSources.Count);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try
                {
                    received = await udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }
                catch (SocketException ex)
                {
                    _lastError = ex.Message;
                    logger.LogWarning(ex, "Syslog listener receive error; continuing.");
                    continue;
                }

                var result = _handler.Handle(received.RemoteEndPoint.Address, received.Buffer);
                if (result.Item is not null)
                {
                    yield return result.Item;
                }
                else if (result.DropReason == DatagramDropReason.SourceNotAllowed)
                {
                    logger.LogWarning(
                        "Syslog datagram dropped from disallowed source {Peer}.",
                        received.RemoteEndPoint.Address);
                }
            }
        }
        finally
        {
            _listening = false;
        }
    }
}
