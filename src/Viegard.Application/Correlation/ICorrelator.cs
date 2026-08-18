using Viegard.Domain.Events;
using Viegard.Domain.Incidents;

namespace Viegard.Application.Correlation;

/// <summary>
/// Folds normalized events into incidents (Flight) by configurable dimensions
/// (source IP, subnet, time window, destination host, URI family, User-Agent).
/// The correlator is a singleton role: exactly one instance runs per
/// deployment (D-0011), independent of the LLM.
/// </summary>
public interface ICorrelator
{
    /// <summary>
    /// Incorporate one normalized event; returns incidents created or updated
    /// as a result (often empty when the event is benign or merely buffered).
    /// </summary>
    ValueTask<IReadOnlyList<Incident>> CorrelateAsync(NormalizedEvent normalizedEvent, CancellationToken cancellationToken = default);
}
