using Microsoft.Extensions.Options;
using Viegard.Application.Audit;
using Viegard.Application.Secrets;
using Viegard.Application.Stores;
using Viegard.Application.Telemetry;
using Viegard.Persistence.InMemory;
using Viegard.PipelineHost.Configuration;
using Viegard.PipelineHost.Workers;

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

builder.Services.AddHostedService<PipelineStartupService>();
builder.Services.AddHostedService<QueueTelemetryPublisher>();

var host = builder.Build();
host.Run();
