using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Viegard.Application.Health;
using Viegard.Application.Sources;
using Viegard.Application.Stores;
using Viegard.Domain;
using Viegard.Domain.Events;
using Viegard.Domain.Health;

namespace Viegard.Sources.MDaemonLogs;

/// <summary>
/// Tails configured MDaemon per-day log files on the satellite host and emits
/// one JSON-wrapped observation per appended non-banner line.
/// </summary>
public sealed class MDaemonLogSource(
    MDaemonSourceOptions options,
    ISourceOffsetStore offsetStore,
    ILogger<MDaemonLogSource> logger) : IDataSource, IHealthContributor
{
    public const string MDaemonSourceType = "mdaemon";

    private long _linesEmitted;
    private int _trackedFiles;
    private DateTimeOffset? _lastScan;
    private string? _lastError;

    public string SourceId => "mdaemon:logs";

    public string SourceType => MDaemonSourceType;

    public string ComponentId => SourceId;

    public Task<ComponentHealth> CheckAsync(CancellationToken cancellationToken = default)
    {
        var healthy = _lastError is null && _lastScan is not null;
        var detail = healthy
            ? $"Tracking {_trackedFiles} file(s); emitted {Interlocked.Read(ref _linesEmitted)} line(s)."
            : _lastError ?? "Not scanned yet.";

        return Task.FromResult(new ComponentHealth
        {
            ComponentId = ComponentId,
            Status = healthy ? HealthStatus.Healthy : HealthStatus.Degraded,
            Detail = detail,
            CheckedAt = DateTimeOffset.UtcNow,
        });
    }

    public async IAsyncEnumerable<ObservedItem> ObserveAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            IReadOnlyList<ObservedItem> batch;
            try
            {
                batch = await ScanOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _lastError = ex.Message;
                logger.LogWarning(ex, "MDaemon log source scan failed; will retry after poll interval.");
                batch = [];
            }

            foreach (var item in batch)
            {
                yield return item;
            }

            try
            {
                await Task.Delay(options.PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }

    private async Task<IReadOnlyList<ObservedItem>> ScanOnceAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(options.LogDirectory))
        {
            _lastError = $"Log directory '{options.LogDirectory}' does not exist.";
            return [];
        }

        var items = new List<ObservedItem>();
        var tracked = 0;

        foreach (var fileOption in options.Files)
        {
            if (!MDaemonSourceOptionsValidator.TryParseLogKind(fileOption.LogKind, out var logKind))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(options.LogDirectory, fileOption.Pattern, SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                tracked++;
                var fileItems = await ReadNewLinesAsync(path, logKind, cancellationToken).ConfigureAwait(false);
                items.AddRange(fileItems);
            }
        }

        _trackedFiles = tracked;
        _lastScan = DateTimeOffset.UtcNow;
        _lastError = null;
        if (items.Count > 0)
        {
            Interlocked.Add(ref _linesEmitted, items.Count);
        }

        return items;
    }

    private async Task<IReadOnlyList<ObservedItem>> ReadNewLinesAsync(
        string path,
        MDaemonLogKind logKind,
        CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(path);
        var offsetKey = "offset:" + fileName;
        var stored = await offsetStore.GetAsync(SourceId, offsetKey, cancellationToken).ConfigureAwait(false);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        var hasOffset = long.TryParse(stored, out var offset) && offset >= 0;

        if (!hasOffset)
        {
            offset = options.IngestExistingOnFirstRun ? 0 : length;
            await offsetStore.SetAsync(SourceId, offsetKey, offset.ToString(), cancellationToken).ConfigureAwait(false);
            if (offset == length)
            {
                return [];
            }
        }
        else if (offset > length)
        {
            logger.LogInformation("MDaemon log file {FileName} shrank from offset {Offset} to {Length}; reading from start.", fileName, offset, length);
            offset = 0;
        }

        if (offset == length)
        {
            return [];
        }

        var byteCount = length - offset;
        if (byteCount > int.MaxValue)
        {
            _lastError = $"File '{fileName}' has more than {int.MaxValue} unread bytes; skipping until next scan.";
            logger.LogWarning("MDaemon log file {FileName} has too many unread bytes ({ByteCount}); skipping this scan.", fileName, byteCount);
            return [];
        }

        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[byteCount];
        var read = 0;
        while (read < buffer.Length)
        {
            var current = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), cancellationToken).ConfigureAwait(false);
            if (current == 0)
            {
                break;
            }

            read += current;
        }

        if (read == 0)
        {
            return [];
        }

        var capturedAt = DateTimeOffset.UtcNow;
        var text = Encoding.UTF8.GetString(buffer, 0, read);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var items = new List<ObservedItem>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                continue;
            }

            if (logKind != MDaemonLogKind.DynamicScreening && SessionTranscriptParser.IsBannerOrSeparator(line))
            {
                continue;
            }

            if (!options.IncludeNoise
                && logKind == MDaemonLogKind.DynamicScreening
                && DynScrnParser.Parse(line)?.IsNoise == true)
            {
                continue;
            }

            var dto = new MDaemonLineDto
            {
                SchemaVersion = 1,
                LogKind = logKind,
                FileName = fileName,
                LineText = line,
                CapturedAt = capturedAt,
            };

            items.Add(new ObservedItem
            {
                Observation = new RawObservation
                {
                    Id = ViegardId.New(),
                    SourceId = SourceId,
                    SourceType = MDaemonSourceType,
                    ObservedAt = capturedAt,
                    PayloadReference = $"mdaemon/{fileName}/{length}/{i}",
                    IngestOffset = length.ToString(),
                },
                RawPayload = JsonSerializer.Serialize(dto, MDaemonJson.SerializerOptions),
            });
        }

        await offsetStore.SetAsync(SourceId, offsetKey, length.ToString(), cancellationToken).ConfigureAwait(false);
        return items;
    }
}