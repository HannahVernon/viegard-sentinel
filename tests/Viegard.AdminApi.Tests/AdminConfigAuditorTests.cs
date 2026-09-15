using Microsoft.Extensions.Logging;
using Viegard.AdminApi.Auth;
using Viegard.Application.Audit;
using Viegard.Application.Retention;
using Viegard.Application.Stores;
using Viegard.Domain;
using Viegard.Domain.Audit;
using Viegard.Domain.Configuration;

namespace Viegard.AdminApi.Tests;

public sealed class AdminConfigAuditorTests
{
    [Fact]
    public async Task Signature_write_audit_record_contains_actor_action_and_before_after()
    {
        var ledger = new RecordingAuditLedger();
        var auditor = new AdminConfigAuditor(ledger, new SilentLogger<AdminConfigAuditor>());
        var before = Signature("before");
        var after = before with { Pattern = "after", Version = before.Version + 1 };

        await auditor.RecordSignatureWriteAsync("updated", "hannah", before, after);

        var record = Assert.Single(ledger.Records);
        Assert.Equal(PipelineStage.Admin, record.Stage);
        Assert.Contains("hannah", record.Summary, StringComparison.Ordinal);
        Assert.Contains("updated", record.Summary, StringComparison.Ordinal);
        Assert.Contains("before", record.DetailJson, StringComparison.Ordinal);
        Assert.Contains("after", record.DetailJson, StringComparison.Ordinal);
        Assert.Contains("\"username\":\"hannah\"", record.DetailJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Retention_settings_write_audit_record_contains_old_and_new_values()
    {
        var ledger = new RecordingAuditLedger();
        var auditor = new AdminConfigAuditor(ledger, new SilentLogger<AdminConfigAuditor>());
        var before = new RetentionSettings
        {
            Id = RetentionSettings.FixedId,
            EventsDays = 90,
            Version = 1,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "hannah",
        };
        var after = before with
        {
            EventsDays = 30,
            Version = 2,
        };

        await auditor.RecordRetentionSettingsWriteAsync("hannah", before, after);

        var record = Assert.Single(ledger.Records);
        Assert.Equal(PipelineStage.Admin, record.Stage);
        Assert.Contains("hannah", record.Summary, StringComparison.Ordinal);
        Assert.Contains("changed retention settings", record.Summary, StringComparison.Ordinal);
        Assert.Contains("RetentionSettingsChanged", record.DetailJson, StringComparison.Ordinal);
        Assert.Contains("\"eventsDays\":90", record.DetailJson, StringComparison.Ordinal);
        Assert.Contains("\"eventsDays\":30", record.DetailJson, StringComparison.Ordinal);
    }

    private static CustomSignature Signature(string pattern) => new()
    {
        Id = ViegardId.New(),
        Name = "audit-test",
        Enabled = true,
        Target = CustomSignatureTarget.HttpUri,
        MatchType = CustomSignatureMatchType.Contains,
        Pattern = pattern,
        Category = "test",
        Severity = 3,
        EvidenceWeight = 1.0,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        UpdatedBy = "hannah",
        Version = 1,
    };

    private sealed class RecordingAuditLedger : IAuditLedger
    {
        public List<AuditRecord> Records { get; } = [];

        public ValueTask AppendAsync(AuditRecord record, CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return ValueTask.CompletedTask;
        }

        public ValueTask<KeysetPage<AuditRecord>> ListPageAsync(
            Guid? beforeId,
            int pageSize,
            AuditListFilter? filter = null,
            ListSort<AuditSortColumn>? sort = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new KeysetPage<AuditRecord>(Records, null, Records.Count, 0));
    }

    private sealed class SilentLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }
}
