using System.Text.Json;
using Viegard.Actions.MikroTik;
using Viegard.Application.Configuration;
using Viegard.Application.Audit;
using Viegard.Application.Policy;
using Viegard.Application.Retention;
using Viegard.Domain;
using Viegard.Domain.Audit;
using Viegard.Domain.Configuration;
using Viegard.Domain.Decisions;

namespace Viegard.AdminApi.Auth;

public sealed class AdminConfigAuditor(IAuditLedger auditLedger, ILogger<AdminConfigAuditor> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async ValueTask RecordSignatureWriteAsync(
        string action,
        string username,
        CustomSignature? before,
        CustomSignature? after,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var label = after?.Name ?? before?.Name ?? "unknown";
            await auditLedger.AppendAsync(new AuditRecord
            {
                Id = ViegardId.New(),
                Timestamp = DateTimeOffset.UtcNow,
                Stage = PipelineStage.Admin,
                Summary = $"Admin config write by {username}: {action} custom signature '{label}'.",
                SourceId = "admin:config",
                DetailJson = JsonSerializer.Serialize(new
                {
                    Action = action,
                    Username = username,
                    BeforeTerms = SignatureTerms(before),
                    AfterTerms = SignatureTerms(after),
                    Before = before,
                    After = after,
                }, JsonOptions),
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to append admin configuration audit record.");
        }
    }

    private static IReadOnlyList<string>? SignatureTerms(CustomSignature? signature)
    {
        if (signature is null)
        {
            return null;
        }

        var terms = new List<string> { signature.Pattern };
        terms.AddRange(signature.AdditionalPatterns ?? []);
        return terms;
    }

    public async ValueTask RecordRetentionSettingsWriteAsync(
        string username,
        RetentionSettings? before,
        RetentionSettings? after,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await auditLedger.AppendAsync(new AuditRecord
            {
                Id = ViegardId.New(),
                Timestamp = DateTimeOffset.UtcNow,
                Stage = PipelineStage.Admin,
                Summary = $"Admin config write by {username}: changed retention settings.",
                SourceId = "admin:config",
                DetailJson = JsonSerializer.Serialize(new
                {
                    Kind = "RetentionSettingsChanged",
                    Username = username,
                    Before = before,
                    After = after,
                }, JsonOptions),
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to append admin configuration audit record.");
        }
    }

    public async ValueTask RecordIngestionFiltersWriteAsync(
        string username,
        string sourceType,
        IReadOnlyList<IngestionFilter> before,
        IReadOnlyList<IngestionFilter> after,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await auditLedger.AppendAsync(new AuditRecord
            {
                Id = ViegardId.New(),
                Timestamp = DateTimeOffset.UtcNow,
                Stage = PipelineStage.Admin,
                Summary = $"Admin config write by {username}: changed ingestion filters for {sourceType}.",
                SourceId = "admin:config",
                DetailJson = JsonSerializer.Serialize(new
                {
                    Kind = "IngestionFiltersChanged",
                    Username = username,
                    SourceType = sourceType,
                    BeforeSuppressed = SuppressedKinds(before),
                    AfterSuppressed = SuppressedKinds(after),
                    Before = before,
                    After = after,
                }, JsonOptions),
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to append admin configuration audit record.");
        }
    }

    private static IReadOnlyList<string> SuppressedKinds(IReadOnlyList<IngestionFilter> filters) =>
        filters
            .Where(filter => filter.Suppressed)
            .Select(filter => filter.EventKind)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    public async ValueTask RecordPolicyThresholdSettingsWriteAsync(
        string username,
        PolicyThresholdSettings? before,
        PolicyThresholdSettings? after,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await auditLedger.AppendAsync(new AuditRecord
            {
                Id = ViegardId.New(),
                Timestamp = DateTimeOffset.UtcNow,
                Stage = PipelineStage.Admin,
                Summary = $"Admin config write by {username}: changed policy threshold settings.",
                SourceId = "admin:config",
                DetailJson = JsonSerializer.Serialize(new
                {
                    Kind = "PolicyThresholdSettingsChanged",
                    Username = username,
                    Before = before,
                    After = after,
                }, JsonOptions),
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to append admin configuration audit record.");
        }
    }

    public async ValueTask RecordPolicyPostureSettingsWriteAsync(
        string username,
        PolicyPostureSettings? before,
        PolicyPostureSettings? after,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await auditLedger.AppendAsync(new AuditRecord
            {
                Id = ViegardId.New(),
                Timestamp = DateTimeOffset.UtcNow,
                Stage = PipelineStage.Admin,
                Summary = $"Admin config write by {username}: changed policy posture settings.",
                SourceId = "admin:config",
                DetailJson = JsonSerializer.Serialize(new
                {
                    Kind = "PolicyPostureSettingsChanged",
                    Username = username,
                    Before = before,
                    After = after,
                }, JsonOptions),
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to append admin configuration audit record.");
        }
    }

    public async ValueTask RecordSatelliteRoleWriteAsync(
        string kind,
        string username,
        string roleName,
        string grantSummary,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await auditLedger.AppendAsync(new AuditRecord
            {
                Id = ViegardId.New(),
                Timestamp = DateTimeOffset.UtcNow,
                Stage = PipelineStage.Admin,
                Summary = $"Admin config write by {username}: {kind} for satellite role '{roleName}'.",
                SourceId = "admin:config",
                DetailJson = JsonSerializer.Serialize(new
                {
                    Kind = kind,
                    Username = username,
                    RoleName = roleName,
                    GrantSummary = grantSummary,
                }, JsonOptions),
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to append admin configuration audit record.");
        }
    }

    public async ValueTask RecordRouterWriteAsync(
        string kind,
        string username,
        MikroTikRouter? before,
        MikroTikRouter? after,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var label = after?.Name ?? before?.Name ?? "unknown";
            await auditLedger.AppendAsync(new AuditRecord
            {
                Id = ViegardId.New(),
                Timestamp = DateTimeOffset.UtcNow,
                Stage = PipelineStage.Admin,
                Summary = $"Admin config write by {username}: {kind} for router '{label}'.",
                SourceId = "admin:config",
                DetailJson = JsonSerializer.Serialize(new
                {
                    Kind = kind,
                    Username = username,
                    Before = before,
                    After = after,
                }, JsonOptions),
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to append admin configuration audit record.");
        }
    }

    public async ValueTask RecordRouterProbeAsync(
        string kind,
        string username,
        string routerName,
        string outcome,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await auditLedger.AppendAsync(new AuditRecord
            {
                Id = ViegardId.New(),
                Timestamp = DateTimeOffset.UtcNow,
                Stage = PipelineStage.Admin,
                Summary = $"Admin config action by {username}: {kind} for router '{routerName}'.",
                SourceId = "admin:config",
                DetailJson = JsonSerializer.Serialize(new
                {
                    Kind = kind,
                    Username = username,
                    RouterName = routerName,
                    Outcome = outcome,
                }, JsonOptions),
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to append admin configuration audit record.");
        }
    }

    public async ValueTask RecordHostUpgradeRequestedAsync(
        string username,
        string target,
        Guid commandId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await auditLedger.AppendAsync(new AuditRecord
            {
                Id = ViegardId.New(),
                Timestamp = DateTimeOffset.UtcNow,
                Stage = PipelineStage.Admin,
                Summary = $"Admin config write by {username}: HostUpgradeRequested for target '{target}'.",
                SourceId = "admin:config",
                DetailJson = JsonSerializer.Serialize(new
                {
                    Target = target,
                    CommandId = commandId,
                }, JsonOptions),
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to append admin configuration audit record.");
        }
    }

    public async ValueTask RecordDecisionReviewAsync(
        string username,
        Guid decisionId,
        DecisionReviewOutcome verdict,
        string? ip,
        string? duration,
        Guid? actionId,
        string outcome,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await auditLedger.AppendAsync(new AuditRecord
            {
                Id = ViegardId.New(),
                Timestamp = DateTimeOffset.UtcNow,
                Stage = PipelineStage.Admin,
                Summary = $"Admin decision review by {username}: {verdict} decision {decisionId:N}.",
                SourceId = "admin:decisions",
                DecisionId = decisionId,
                ActionId = actionId,
                DetailJson = JsonSerializer.Serialize(new
                {
                    Kind = "DecisionReviewed",
                    Username = username,
                    DecisionId = decisionId,
                    Verdict = verdict.ToString(),
                    Ip = ip,
                    Duration = duration,
                    ActionId = actionId,
                    Outcome = outcome,
                }, JsonOptions),
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to append admin decision review audit record.");
        }
    }

    public async ValueTask RecordAdminErrorsClearedAsync(
        string username,
        long removedCount,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await auditLedger.AppendAsync(new AuditRecord
            {
                Id = ViegardId.New(),
                Timestamp = DateTimeOffset.UtcNow,
                Stage = PipelineStage.Admin,
                Summary = $"Admin config write by {username}: cleared {removedCount} captured admin errors.",
                SourceId = "admin:config",
                DetailJson = JsonSerializer.Serialize(new
                {
                    Kind = "AdminErrorsCleared",
                    Username = username,
                    RemovedCount = removedCount,
                }, JsonOptions),
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to append admin configuration audit record.");
        }
    }

    public async ValueTask RecordUnbanAsync(
        string username,
        string ip,
        Guid decisionId,
        Guid actionId,
        string outcome,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await auditLedger.AppendAsync(new AuditRecord
            {
                Id = ViegardId.New(),
                Timestamp = DateTimeOffset.UtcNow,
                Stage = PipelineStage.Admin,
                Summary = $"Admin ban action by {username}: remove-ban for {ip}.",
                SourceId = "admin:bans",
                DecisionId = decisionId,
                ActionId = actionId,
                DetailJson = JsonSerializer.Serialize(new
                {
                    Kind = "UnbanRequested",
                    Username = username,
                    Ip = ip,
                    DecisionId = decisionId,
                    ActionId = actionId,
                    ProviderId = MikroTikBanActionProvider.MikroTikProviderId,
                    OperationId = MikroTikBanActionProvider.RemoveBanOperationId,
                    Outcome = outcome,
                }, JsonOptions),
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to append admin unban audit record.");
        }
    }
}
