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
}
