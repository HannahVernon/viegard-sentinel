using Microsoft.Extensions.Options;
using Viegard.Application.Audit;
using Viegard.Application.Classifiers;
using Viegard.Application.Correlation;
using Viegard.Application.Detection;
using Viegard.Application.Policy;
using Viegard.Application.Queues;
using Viegard.Application.Secrets;
using Viegard.Application.Sources;
using Viegard.Application.Stores;
using Viegard.Application.Telemetry;
using Viegard.Persistence.InMemory;
using Viegard.Persistence.Postgres;
using Viegard.PipelineHost.Configuration;
using Viegard.PipelineHost.Workers;
using Viegard.Sources.Imap;
using Viegard.Sources.MDaemonLogs;
using Viegard.Sources.Syslog;

var builder = Host.CreateApplicationBuilder(args);

// Host topology (roles this instance runs; D-0011).  Invalid topology fails startup.
builder.Services
    .AddOptions<ViegardHostOptions>()
    .Bind(builder.Configuration.GetSection(ViegardHostOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<ViegardHostOptions>, ViegardHostOptionsValidator>();

builder.Services
    .AddOptions<DetectionOptions>()
    .Bind(builder.Configuration.GetSection(DetectionOptions.SectionName))
    .Validate(o => o.MaxInputCharsToScan > 0, "Detection MaxInputCharsToScan must be positive.")
    .Validate(o => o.MaxEvidencePerRule > 0, "Detection MaxEvidencePerRule must be positive.")
    .ValidateOnStart();

builder.Services
    .AddOptions<CorrelationOptions>()
    .Bind(builder.Configuration.GetSection(CorrelationOptions.SectionName))
    .Validate(o => o.WindowDuration > TimeSpan.Zero, "Correlation WindowDuration must be positive.")
    .Validate(o => o.MaxEventIdsPerIncident > 0, "Correlation MaxEventIdsPerIncident must be positive.")
    .Validate(o => o.MaxEvidenceItemsPerIncident > 0, "Correlation MaxEvidenceItemsPerIncident must be positive.")
    .ValidateOnStart();

builder.Services
    .AddOptions<PolicyOptions>()
    .Bind(builder.Configuration.GetSection(PolicyOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<PolicyOptions>, PolicyOptionsValidator>();
builder.Services.AddSingleton(sp =>
    new ProtectedAddressList(sp.GetRequiredService<IOptions<PolicyOptions>>().Value.ProtectedCidrs));
builder.Services.AddSingleton<IGuardrailStateStore, InMemoryGuardrailStateStore>();

builder.Services
    .AddOptions<ClassifierOptions>()
    .Bind(builder.Configuration.GetSection(ClassifierOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<ClassifierOptions>, ClassifierOptionsValidator>();

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

// Persistence provider (D-0024): "postgres" for durable shared persistence,
// "inmemory" for development without a database.
var persistenceProvider = builder.Configuration["Viegard:Persistence:Provider"] ?? "inmemory";
switch (persistenceProvider)
{
    case "postgres":
        builder.Services.AddViegardPostgresPersistence(builder.Configuration);
        break;

    case "inmemory":
        builder.Services.AddSingleton<IRawObservationStore, InMemoryRawObservationStore>();
        builder.Services.AddSingleton<IEventStore, InMemoryEventStore>();
        builder.Services.AddSingleton<IIncidentStore, InMemoryIncidentStore>();
        builder.Services.AddSingleton<IClassificationStore, InMemoryClassificationStore>();
        builder.Services.AddSingleton<IDecisionStore, InMemoryDecisionStore>();
        builder.Services.AddSingleton<IActionStore, InMemoryActionStore>();
        builder.Services.AddSingleton<ICorrectionStore, InMemoryCorrectionStore>();
        builder.Services.AddSingleton<IAdminUserStore, InMemoryAdminUserStore>();
        builder.Services.AddSingleton<IAdminSessionStore, InMemoryAdminSessionStore>();
        builder.Services.AddSingleton<IAuditLedger, InMemoryAuditLedger>();
        builder.Services.AddSingleton<IQueueTelemetryStore, InMemoryQueueTelemetryStore>();
        builder.Services.AddSingleton<ISourceOffsetStore, InMemorySourceOffsetStore>();

        var eventsQueue = new ChannelWorkQueue<Guid>("events");
        var incidentsQueue = new ChannelWorkQueue<IncidentWorkItem>("incidents");
        var classificationsQueue = new ChannelWorkQueue<ClassificationWorkItem>("classifications");
        builder.Services.AddSingleton<IWorkQueue<Guid>>(eventsQueue);
        builder.Services.AddSingleton<IQueueStatsSource>(eventsQueue);
        builder.Services.AddSingleton<IWorkQueue<IncidentWorkItem>>(incidentsQueue);
        builder.Services.AddSingleton<IQueueStatsSource>(incidentsQueue);
        builder.Services.AddSingleton<IWorkQueue<ClassificationWorkItem>>(classificationsQueue);
        builder.Services.AddSingleton<IQueueStatsSource>(classificationsQueue);
        break;

    default:
        throw new InvalidOperationException(
            $"Unknown persistence provider '{persistenceProvider}'.  Supported: inmemory, postgres.");
}

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

// MDaemon flat-file source (D-0013, D-0025; off unless explicitly enabled).
builder.Services
    .AddOptions<MDaemonSourceOptions>()
    .Bind(builder.Configuration.GetSection(MDaemonSourceOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<MDaemonSourceOptions>, MDaemonSourceOptionsValidator>();

var mdaemonOptions = builder.Configuration.GetSection(MDaemonSourceOptions.SectionName).Get<MDaemonSourceOptions>();
if (mdaemonOptions?.Enabled == true)
{
    MDaemonLogSource? mdaemonInstance = null;
    MDaemonLogSource MDaemonFactory(IServiceProvider sp) => mdaemonInstance ??= new MDaemonLogSource(
        sp.GetRequiredService<IOptions<MDaemonSourceOptions>>().Value,
        sp.GetRequiredService<ISourceOffsetStore>(),
        sp.GetRequiredService<ILogger<MDaemonLogSource>>());

    builder.Services.AddSingleton<IDataSource>(MDaemonFactory);
    builder.Services.AddSingleton<Viegard.Application.Health.IHealthContributor>(MDaemonFactory);
    builder.Services.AddSingleton<IEventNormalizer>(sp =>
        new MDaemonEventNormalizer(sp.GetRequiredService<IOptions<MDaemonSourceOptions>>().Value));
}
builder.Services.AddHostedService<QueueTelemetryPublisher>();

// The ingestion worker runs only in instances configured for the sources role (D-0011).
var configuredRoles = builder.Configuration.GetSection($"{ViegardHostOptions.SectionName}:Roles").Get<string[]>() ?? [];
if (configuredRoles.Contains(RoleNames.Sources, StringComparer.OrdinalIgnoreCase))
{
    builder.Services.AddHostedService<IngestionWorker>();
}

// The correlation worker runs only in the singleton correlation role (D-0011).
if (configuredRoles.Contains(RoleNames.Correlation, StringComparer.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IDetectionRule>(sp =>
        new SensitivePathHttpDetectionRule(sp.GetRequiredService<IOptions<DetectionOptions>>().Value));
    builder.Services.AddSingleton<IDetectionRule>(sp =>
        new PathTraversalHttpDetectionRule(sp.GetRequiredService<IOptions<DetectionOptions>>().Value));
    builder.Services.AddSingleton<IDetectionRule>(sp =>
        new SqlInjectionHttpDetectionRule(sp.GetRequiredService<IOptions<DetectionOptions>>().Value));
    builder.Services.AddSingleton<IDetectionRule>(sp =>
        new CommandInjectionHttpDetectionRule(sp.GetRequiredService<IOptions<DetectionOptions>>().Value));
    builder.Services.AddSingleton<IDetectionRule>(sp =>
        new XssHttpDetectionRule(sp.GetRequiredService<IOptions<DetectionOptions>>().Value));
    builder.Services.AddSingleton<IDetectionRule>(sp =>
        new SuspiciousUserAgentHttpDetectionRule(sp.GetRequiredService<IOptions<DetectionOptions>>().Value));
    builder.Services.AddSingleton<IDetectionRule>(sp =>
        new UnusualHttpMethodDetectionRule(sp.GetRequiredService<IOptions<DetectionOptions>>().Value));
    builder.Services.AddSingleton<IDetectionRule>(sp =>
        new ErrorStatusHttpDetectionRule(sp.GetRequiredService<IOptions<DetectionOptions>>().Value));
    builder.Services.AddSingleton<IDetectionRule>(sp =>
        new ExcessiveLinksMailDetectionRule(sp.GetRequiredService<IOptions<DetectionOptions>>().Value));
    builder.Services.AddSingleton<IDetectionRule>(sp =>
        new FromLinkDomainMismatchMailDetectionRule(sp.GetRequiredService<IOptions<DetectionOptions>>().Value));
    builder.Services.AddSingleton<IDetectionRule, ReplyToDomainMismatchMailDetectionRule>();
    builder.Services.AddSingleton<IDetectionRule>(sp =>
        new AttachmentDoubleExtensionMailDetectionRule(sp.GetRequiredService<IOptions<DetectionOptions>>().Value));
    builder.Services.AddSingleton<IDetectionRule>(sp =>
        new ExecutableAttachmentMailDetectionRule(sp.GetRequiredService<IOptions<DetectionOptions>>().Value));
    builder.Services.AddSingleton<IDetectionRule, MDaemonDetectionRule>();
    builder.Services.AddSingleton<ICorrelator>(sp =>
        new TimeWindowCorrelator(
            sp.GetRequiredService<IEnumerable<IDetectionRule>>(),
            sp.GetRequiredService<IIncidentStore>(),
            sp.GetRequiredService<IOptions<CorrelationOptions>>().Value));
    builder.Services.AddHostedService<CorrelationWorker>();
}

// The classification worker runs only in the singleton classification role (D-0011, D-0028).
if (configuredRoles.Contains(RoleNames.Classification, StringComparer.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IClassifier, DeterministicIncidentClassifier>();
    builder.Services.AddHostedService<ClassificationWorker>();
}

// The policy worker runs only in the singleton policy role (D-0011, D-0027).
if (configuredRoles.Contains(RoleNames.Policy, StringComparer.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IPolicyEngine, DefaultPolicyEngine>();
    builder.Services.AddHostedService<PolicyWorker>();
}

var host = builder.Build();

if (persistenceProvider == "postgres")
{
    await host.Services.MigrateViegardDatabaseAsync();
}

host.Run();
