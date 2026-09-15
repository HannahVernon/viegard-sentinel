using System.Text.Json;
using Viegard.Application.Audit;
using Viegard.Application.Retention;
using Viegard.Domain;
using Viegard.Domain.Audit;
using Viegard.Domain.Configuration;

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
}
