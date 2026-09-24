using Viegard.Application.Doh;

namespace Viegard.Persistence.InMemory;

/// <summary>
/// Single-process probe trigger for in-memory mode (development and tests).
/// Because there is no shared database, this only bridges a request to a worker
/// running in the same process; separate in-memory processes cannot signal each
/// other, which is an inherent limitation of in-memory mode.
/// </summary>
public sealed class InMemoryDohProbeTrigger : IDohProbeTrigger, IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public ValueTask RequestAsync(CancellationToken cancellationToken = default)
    {
        // Coalesce: keep the pending count at one so repeated requests before
        // the worker wakes produce a single extra cycle.
        if (_signal.CurrentCount == 0)
        {
            try
            {
                _signal.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<bool> WaitForRequestAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout < TimeSpan.Zero)
        {
            timeout = TimeSpan.Zero;
        }

        return await _signal.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _signal.Dispose();
}
