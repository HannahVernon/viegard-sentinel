using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Viegard.Application.Doh;
using Viegard.Persistence.Postgres.Model;

namespace Viegard.Persistence.Postgres.Stores;

public sealed class PostgresDohReconciliationProposalStore(IDbContextFactory<ViegardDbContext> factory)
    : IDohReconciliationProposalStore
{
    private const int FixedId = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async ValueTask<DohReconciliationProposal?> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.DohReconciliationProposals
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == FixedId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        return JsonSerializer.Deserialize<DohReconciliationProposal>(row.DetailJson, Json);
    }

    public async ValueTask SaveAsync(DohReconciliationProposal proposal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var detailJson = JsonSerializer.Serialize(proposal, Json);
        var generatedAt = proposal.GeneratedAt.ToUniversalTime();

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.DohReconciliationProposals
            .FirstOrDefaultAsync(r => r.Id == FixedId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            db.DohReconciliationProposals.Add(new DohReconciliationProposalRow
            {
                Id = FixedId,
                GeneratedAt = generatedAt,
                DetailJson = detailJson,
            });
        }
        else
        {
            row.GeneratedAt = generatedAt;
            row.DetailJson = detailJson;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
