using Viegard.Application.Doh;

namespace Viegard.Persistence.InMemory;

public sealed class InMemoryDohReconciliationProposalStore : IDohReconciliationProposalStore
{
    private readonly object _sync = new();
    private DohReconciliationProposal? _proposal;

    public ValueTask<DohReconciliationProposal?> GetAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return ValueTask.FromResult(_proposal);
        }
    }

    public ValueTask SaveAsync(DohReconciliationProposal proposal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        lock (_sync)
        {
            _proposal = proposal;
        }

        return ValueTask.CompletedTask;
    }
}
