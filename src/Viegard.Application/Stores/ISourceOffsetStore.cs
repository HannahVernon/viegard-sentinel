namespace Viegard.Application.Stores;

/// <summary>
/// Persistence port for per-source ingestion offsets (e.g., last-seen IMAP
/// UID per folder, log byte offsets), so restarts resume without loss or
/// duplication.
/// </summary>
public interface ISourceOffsetStore
{
    ValueTask<string?> GetAsync(string sourceId, string key, CancellationToken cancellationToken = default);

    ValueTask SetAsync(string sourceId, string key, string value, CancellationToken cancellationToken = default);
}
