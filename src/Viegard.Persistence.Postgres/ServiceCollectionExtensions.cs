using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Viegard.Application.Audit;
using Viegard.Application.Queues;
using Viegard.Application.Secrets;
using Viegard.Application.Stores;
using Viegard.Application.Telemetry;
using Viegard.Persistence.Postgres.Queues;
using Viegard.Persistence.Postgres.Stores;

namespace Viegard.Persistence.Postgres;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the PostgreSQL persistence provider (D-0024): data source
    /// (password via ISecretProvider), DbContext factory, all store ports,
    /// the durable events work queue, and the durable admin command queue.
    /// </summary>
    public static IServiceCollection AddViegardPostgresPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<DatabaseOptions>, DatabaseOptionsValidator>();

        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            var secretProvider = sp.GetRequiredService<ISecretProvider>();

            // Startup-time blocking is acceptable here: the data source is a
            // singleton created once, and hosts cannot run without it.
            var password = secretProvider.GetAsync(options.PasswordSecretName).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException(
                    $"Database password secret '{options.PasswordSecretName}' was not found.");

            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = options.Host,
                Port = options.Port,
                Database = options.Name,
                Username = options.Username,
                Password = password.Reveal(),
            };

            return new NpgsqlDataSourceBuilder(builder.ConnectionString).Build();
        });

        services.AddDbContextFactory<ViegardDbContext>((sp, optionsBuilder) =>
            optionsBuilder.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>()));

        services.AddSingleton<IRawObservationStore, PostgresRawObservationStore>();
        services.AddSingleton<IEventStore, PostgresEventStore>();
        services.AddSingleton<IIncidentStore, PostgresIncidentStore>();
        services.AddSingleton<IClassificationStore, PostgresClassificationStore>();
        services.AddSingleton<IDecisionStore, PostgresDecisionStore>();
        services.AddSingleton<IActionStore, PostgresActionStore>();
        services.AddSingleton<ICorrectionStore, PostgresCorrectionStore>();
        services.AddSingleton<IAuditLedger, PostgresAuditLedger>();
        services.AddSingleton<IQueueTelemetryStore, PostgresQueueTelemetryStore>();
        services.AddSingleton<ISourceOffsetStore, PostgresSourceOffsetStore>();

        // Durable events queue and command queue (broker-semantics port).
        services.AddSingleton<IWorkQueue<Guid>>(sp =>
            new PostgresWorkQueue<Guid>(sp.GetRequiredService<NpgsqlDataSource>(), "events"));
        services.AddSingleton<IQueueStatsSource>(sp => (PostgresWorkQueue<Guid>)sp.GetRequiredService<IWorkQueue<Guid>>());
        services.AddSingleton<ICommandQueue>(sp =>
            new PostgresCommandQueue(sp.GetRequiredService<NpgsqlDataSource>()));

        return services;
    }

    /// <summary>Applies pending migrations when AutoMigrate is enabled.  Call once at host startup.</summary>
    public static async Task MigrateViegardDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var options = services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        if (!options.AutoMigrate)
        {
            return;
        }

        var factory = services.GetRequiredService<IDbContextFactory<ViegardDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }
}
