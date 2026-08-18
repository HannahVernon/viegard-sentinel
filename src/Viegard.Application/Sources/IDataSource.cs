using Viegard.Domain.Events;

namespace Viegard.Application.Sources;

/// <summary>
/// A configured data-source instance (Eyes).  One implementation class per
/// source family; one instance per configured source (e.g., per IMAP account),
/// each with its own credential, offsets, and health.
/// </summary>
public interface IDataSource
{
    /// <summary>Unique identifier of this configured source instance.</summary>
    string SourceId { get; }

    /// <summary>
    /// Continuously produce raw observations until cancelled.  Implementations
    /// must persist ingestion offsets so a restart resumes without loss or
    /// duplication, and must surface malformed input as observations rather
    /// than throwing.
    /// </summary>
    IAsyncEnumerable<RawObservation> ObserveAsync(CancellationToken cancellationToken);
}
