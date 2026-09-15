using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Viegard.Persistence.Postgres;

/// <summary>
/// Design-time factory so `dotnet ef migrations` can build the model without
/// a live database or the host's secret wiring.  The connection string here
/// is a placeholder; migrations are generated offline and applied at host
/// startup (DatabaseOptions.AutoMigrate).
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ViegardDbContext>
{
    public ViegardDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<ViegardDbContext>();
        ViegardDbContextConfiguration.Configure(builder, "Host=localhost;Database=viegard_design;Username=design", "viegard");
        return new ViegardDbContext(builder.Options);
    }
}
