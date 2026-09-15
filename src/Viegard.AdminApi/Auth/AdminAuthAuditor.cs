using System.Text.Json;
using Viegard.Application.Audit;
using Viegard.Application.Logging;
using Viegard.Application.Queues;
using Viegard.Application.Stores;
using Viegard.Domain;
using Viegard.Domain.Audit;
using Viegard.Domain.Events;

namespace Viegard.AdminApi.Auth;

public sealed class AdminAuthAuditor(
    IAuditLedger auditLedger,
    IRawObservationStore rawObservationStore,
    IEventStore eventStore,
    IWorkQueue<Guid> eventQueue,
    ILogger<AdminAuthAuditor> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async ValueTask RecordAsync(
        AdminAuthEventKind kind,
        string? username,
        HttpContext context,
        bool enqueueForCorrelation = false,
        CancellationToken cancellationToken = default)
    {
        var occurredAt = DateTimeOffset.UtcNow;
        var payload = new AdminAuthEvent
        {
            Kind = kind,
            Username = username ?? string.Empty,
            RemoteAddress = context.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            UserAgent = context.Request.Headers.UserAgent.ToString(),
            OccurredAt = occurredAt,
        };

        await TryAppendAuditAsync(payload, cancellationToken).ConfigureAwait(false);
        if (enqueueForCorrelation)
        {
            await TryEnqueuePipelineEventAsync(payload, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask TryAppendAuditAsync(AdminAuthEvent payload, CancellationToken cancellationToken)
    {
        try
        {
            await auditLedger.AppendAsync(new AuditRecord
            {
                Id = ViegardId.New(),
                Timestamp = payload.OccurredAt,
                Stage = PipelineStage.Admin,
                Summary = $"Admin authentication event: {payload.Kind}.",
                SourceId = "admin:auth",
                DetailJson = JsonSerializer.Serialize(payload, JsonOptions),
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to append admin authentication audit record.");
        }
    }

    private async ValueTask TryEnqueuePipelineEventAsync(AdminAuthEvent payload, CancellationToken cancellationToken)
    {
        try
        {
            var observation = new RawObservation
            {
                Id = ViegardId.New(),
                SourceId = "admin:auth",
                SourceType = "admin",
                ObservedAt = payload.OccurredAt,
                PayloadReference = $"admin-auth:{ViegardId.New()}",
            };
            await rawObservationStore.AddAsync(observation, JsonSerializer.Serialize(payload, JsonOptions), cancellationToken)
                .ConfigureAwait(false);

            var normalizedEvent = new NormalizedEvent
            {
                Id = ViegardId.New(),
                SourceId = "admin:auth",
                SourceType = "admin",
                OccurredAt = payload.OccurredAt,
                Entities =
                [
                    new EntityRef(EntityKind.IpAddress, payload.RemoteAddress),
                    new EntityRef(EntityKind.UserAgent, payload.UserAgent),
                    new EntityRef(EntityKind.UserIdentity, payload.Username),
                ],
                Payload = payload,
                RawObservationId = observation.Id,
            };
            await eventStore.AddAsync(normalizedEvent, cancellationToken).ConfigureAwait(false);
            await eventQueue.EnqueueAsync(normalizedEvent.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Failed to enqueue admin authentication event {Kind} for {Username}.",
                payload.Kind,
                LogSanitizer.Sanitize(payload.Username));
        }
    }
}
