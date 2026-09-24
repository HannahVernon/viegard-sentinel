namespace Viegard.Application.Doh;

/// <summary>
/// Cross-process signal that requests an immediate DoH probe cycle, so an
/// operator can force a reprobe from the Admin UI without waiting for the
/// periodic probe interval.  The request path (Admin UI/API) and the wait path
/// (the probe worker) run in separate processes, so implementations bridge them
/// through the persistence layer (Postgres LISTEN/NOTIFY in production).
/// </summary>
public interface IDohProbeTrigger
{
    /// <summary>
    /// Requests that the probe worker run a cycle as soon as possible.
    /// Requests are coalesced: several requests that arrive before the worker
    /// wakes result in a single extra cycle.
    /// </summary>
    ValueTask RequestAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for a probe request.  Returns
    /// <see langword="true"/> when a request arrived, <see langword="false"/>
    /// when the wait timed out or the underlying signal was unavailable.  The
    /// worker runs a cycle on either result, so the timeout doubles as the
    /// periodic probe interval.
    /// </summary>
    ValueTask<bool> WaitForRequestAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}
