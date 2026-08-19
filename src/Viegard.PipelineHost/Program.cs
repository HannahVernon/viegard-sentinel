using Microsoft.Extensions.Options;
using Viegard.Application.Audit;
using Viegard.Application.Queues;
using Viegard.Application.Secrets;
using Viegard.Application.Sources;
using Viegard.Application.Stores;
using Viegard.Application.Telemetry;
using Viegard.Persistence.InMemory;
using Viegard.PipelineHost.Configuration;
using Viegard.PipelineHost.Workers;
using Viegard.Sources.Imap;
using Viegard.Sources.Syslog;

var builder = Host.CreateApplicationBuilder(args);

// Host topology (roles this instance runs; D-0011).  Invalid topology fails startup.
builder.Services
    .AddOptions<ViegardHostOptions>()
    .Bind(builder.Configuration.GetSection(ViegardHostOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<ViegardHostOptions>, ViegardHostOptionsValidator>();

// Secrets (D-0006): mounted secret files in production, user-secrets-backed
// configuration in development.  Selection is configuration, never code.
var secretProviderKind = builder.Configuration["Viegard:Secrets:Provider"] ?? "configuration";
switch (secretProviderKind)
{
    case "file":
        var secretsDirectory = builder.Configuration["Viegard:Secrets:Directory"]
            ?? throw new InvalidOperationException(
                "Viegard:Secrets:Directory must be configured when Viegard:Secrets:Provider is 'file'.");
        builder.Services.AddSingleton<ISecretProvider>(new FileSecretProvider(secretsDirectory));
        break;
    case "configuration":
        builder.Services.AddSingleton<ISecretProvider>(sp =>
            new ConfigurationSecretProvider(builder.Configuration));
        break;
    default:
        throw new InvalidOperationException(
            $"Unknown secret provider '{secretProviderKind}'.  Supported: file, configuration.");
}

// Development-only in-memory persistence until the database is chosen (D-0004).
builder.Services.AddSingleton<IRawObservationStore, InMemoryRawObservationStore>();
builder.Services.AddSingleton<IEventStore, InMemoryEventStore>();
builder.Services.AddSingleton<IIncidentStore, InMemoryIncidentStore>();
builder.Services.AddSingleton<IClassificationStore, InMemoryClassificationStore>();
builder.Services.AddSingleton<IDecisionStore, InMemoryDecisionStore>();
builder.Services.AddSingleton<IActionStore, InMemoryActionStore>();
builder.Services.AddSingleton<ICorrectionStore, InMemoryCorrectionStore>();
builder.Services.AddSingleton<IAuditLedger, InMemoryAuditLedger>();
builder.Services.AddSingleton<IQueueTelemetryStore, InMemoryQueueTelemetryStore>();
builder.Services.AddSingleton<ISourceOffsetStore, InMemorySourceOffsetStore>();

// Pipeline queues (in-process for now; broker-ready port per assumption 3).
var eventsQueue = new ChannelWorkQueue<Guid>("events");
builder.Services.AddSingleton<IWorkQueue<Guid>>(eventsQueue);
builder.Services.AddSingleton<IQueueStatsSource>(eventsQueue);

// IMAP source module (Phase 4; D-0019..D-0022).  Account configuration
// (hosts, usernames) is environment-specific: in development it lives in
// user-secrets, never in committed appsettings.
builder.Services
    .AddOptions<ImapSourceOptions>()
    .Bind(builder.Configuration.GetSection(ImapSourceOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<ImapSourceOptions>, ImapSourceOptionsValidator>();
builder.Services.AddSingleton<IEventNormalizer, ImapEventNormalizer>();

var imapAccounts = builder.Configuration.GetSection(ImapSourceOptions.SectionName).Get<ImapSourceOptions>()?.Accounts ?? [];
foreach (var configuredAccount in imapAccounts)
{
    var account = configuredAccount;
    ImapMailSource? instance = null;
    ImapMailSource Factory(IServiceProvider sp) => instance ??= new ImapMailSource(
        account,
        sp.GetRequiredService<ISecretProvider>(),
        sp.GetRequiredService<ISourceOffsetStore>(),
        sp.GetRequiredService<ILogger<ImapMailSource>>());

    builder.Services.AddSingleton<IDataSource>(Factory);
    builder.Services.AddSingleton<Viegard.Application.Health.IHealthContributor>(Factory);
}

builder.Services.AddHostedService<PipelineStartupService>();

// Syslog listener source (D-0023; off unless explicitly enabled with a
// non-empty source allowlist).
builder.Services
    .AddOptions<SyslogSourceOptions>()
    .Bind(builder.Configuration.GetSection(SyslogSourceOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<SyslogSourceOptions>, SyslogSourceOptionsValidator>();

var syslogOptions = builder.Configuration.GetSection(SyslogSourceOptions.SectionName).Get<SyslogSourceOptions>();
if (syslogOptions?.Enabled == true)
{
    SyslogUdpSource? syslogInstance = null;
    SyslogUdpSource SyslogFactory(IServiceProvider sp) => syslogInstance ??= new SyslogUdpSource(
        sp.GetRequiredService<IOptions<SyslogSourceOptions>>().Value,
        sp.GetRequiredService<ILogger<SyslogUdpSource>>());

    builder.Services.AddSingleton<IDataSource>(SyslogFactory);
    builder.Services.AddSingleton<Viegard.Application.Health.IHealthContributor>(SyslogFactory);
    builder.Services.AddSingleton<IEventNormalizer>(sp =>
        new SyslogEventNormalizer(sp.GetRequiredService<IOptions<SyslogSourceOptions>>().Value));
}
builder.Services.AddHostedService<QueueTelemetryPublisher>();

// The ingestion worker runs only in instances configured for the sources role (D-0011).
var configuredRoles = builder.Configuration.GetSection($"{ViegardHostOptions.SectionName}:Roles").Get<string[]>() ?? [];
if (configuredRoles.Contains(RoleNames.Sources, StringComparer.OrdinalIgnoreCase))
{
    builder.Services.AddHostedService<IngestionWorker>();
}

var host = builder.Build();
host.Run();
