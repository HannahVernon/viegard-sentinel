using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Viegard.Application.Doh;
using Viegard.Persistence.Postgres.Model;

namespace Viegard.Persistence.Postgres.Stores;

public sealed class PostgresDohProbeSummaryStore(IDbContextFactory<ViegardDbContext> factory)
    : IDohProbeSummaryStore
{
    private const int FixedId = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async ValueTask<DohProbeSummarySnapshot?> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.DohProbeSummaries
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == FixedId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        return JsonSerializer.Deserialize<DohProbeSummarySnapshot>(row.DetailJson, Json);
    }

    public async ValueTask SaveCurrentAsync(DohProbeOutcomeSummary current, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.DohProbeSummaries
            .FirstOrDefaultAsync(r => r.Id == FixedId, cancellationToken)
            .ConfigureAwait(false);

        var prior = row is null
            ? null
            : JsonSerializer.Deserialize<DohProbeSummarySnapshot>(row.DetailJson, Json)?.Current;

        var snapshot = new DohProbeSummarySnapshot { Current = current, Prior = prior };
        var detailJson = JsonSerializer.Serialize(snapshot, Json);
        var generatedAt = current.GeneratedAt.ToUniversalTime();

        if (row is null)
        {
            db.DohProbeSummaries.Add(new DohProbeSummaryRow
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
