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
                // All Viegard objects live in the configured schema; the
                // model and raw SQL are schema-agnostic and follow the
                // search path.
                SearchPath = options.Schema,
            };

            return new NpgsqlDataSourceBuilder(builder.ConnectionString).Build();
        });

        services.AddDbContextFactory<ViegardDbContext>((sp, optionsBuilder) =>
            ViegardDbContextConfiguration.Configure(
                optionsBuilder,
                sp.GetRequiredService<NpgsqlDataSource>(),
                sp.GetRequiredService<IOptions<DatabaseOptions>>().Value.Schema));

        // Reference-table resolver (D-0031): process-lifetime caches over
        // the insert-only sources/classifiers/policies/action_providers.
        services.AddSingleton<ReferenceResolver>();

        services.AddSingleton<IRawObservationStore, PostgresRawObservationStore>();
        services.AddSingleton<IEventStore, PostgresEventStore>();
        services.AddSingleton<IIncidentStore, PostgresIncidentStore>();
        services.AddSingleton<IClassificationStore, PostgresClassificationStore>();
        services.AddSingleton<IDecisionStore, PostgresDecisionStore>();
        services.AddSingleton<IActionStore, PostgresActionStore>();
        services.AddSingleton<ICorrectionStore, PostgresCorrectionStore>();
        services.AddSingleton<IAdminUserStore, PostgresAdminUserStore>();
        services.AddSingleton<IAdminSessionStore, PostgresAdminSessionStore>();
        services.AddSingleton<IAuditLedger, PostgresAuditLedger>();
        services.AddSingleton<IQueueTelemetryStore, PostgresQueueTelemetryStore>();
        services.AddSingleton<ISourceOffsetStore, PostgresSourceOffsetStore>();

        // Durable events queue and command queue (broker-semantics port).
        services.AddSingleton<IWorkQueue<Guid>>(sp =>
            new PostgresWorkQueue<Guid>(sp.GetRequiredService<NpgsqlDataSource>(), "events"));
        services.AddSingleton<IQueueStatsSource>(sp => (PostgresWorkQueue<Guid>)sp.GetRequiredService<IWorkQueue<Guid>>());
        services.AddSingleton<IWorkQueue<IncidentWorkItem>>(sp =>
            new PostgresWorkQueue<IncidentWorkItem>(sp.GetRequiredService<NpgsqlDataSource>(), "incidents"));
        services.AddSingleton<IQueueStatsSource>(sp =>
            (PostgresWorkQueue<IncidentWorkItem>)sp.GetRequiredService<IWorkQueue<IncidentWorkItem>>());
        services.AddSingleton<IWorkQueue<ClassificationWorkItem>>(sp =>
            new PostgresWorkQueue<ClassificationWorkItem>(sp.GetRequiredService<NpgsqlDataSource>(), "classifications"));
        services.AddSingleton<IQueueStatsSource>(sp =>
            (PostgresWorkQueue<ClassificationWorkItem>)sp.GetRequiredService<IWorkQueue<ClassificationWorkItem>>());
        services.AddSingleton<ICommandQueue>(sp =>
            new PostgresCommandQueue(sp.GetRequiredService<NpgsqlDataSource>()));

        return services;
    }

    /// <summary>Creates the configured schema if needed and applies pending migrations when AutoMigrate is enabled.  Call once at host startup.</summary>
    public static async Task MigrateViegardDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var options = services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        if (!options.AutoMigrate)
        {
            return;
        }

        // The schema name is validated at startup against ^[a-z][a-z0-9_]*$
        // (DatabaseOptionsValidator); re-check here as defense in depth
        // before it participates in DDL.
        if (!DatabaseOptionsValidator.IsSafeSchemaName(options.Schema))
        {
            throw new InvalidOperationException($"Refusing to create schema from unsafe name '{options.Schema}'.");
        }

        var dataSource = services.GetRequiredService<NpgsqlDataSource>();
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"CREATE SCHEMA IF NOT EXISTS {options.Schema}";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var factory = services.GetRequiredService<IDbContextFactory<ViegardDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }
}
