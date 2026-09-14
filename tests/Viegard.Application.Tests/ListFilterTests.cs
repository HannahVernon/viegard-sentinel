using Viegard.Application.Stores;
using Viegard.Domain;
using Viegard.Domain.Audit;
using Viegard.Domain.Configuration;
using Viegard.Domain.Incidents;
using Viegard.Persistence.InMemory;

namespace Viegard.Application.Tests;

public sealed class ListFilterTests
{
    [Fact]
    public void CustomSignatureFilter_matches_name_pattern_and_category_case_insensitively()
    {
        var signature = Signature(
            name: "Aftership referral",
            pattern: "ref=aftership",
            category: "referral-bot");
        var other = Signature(name: "admin", pattern: "/admin", category: "operator");
        var signatures = new[] { signature, other };

        Assert.Equal([signature.Id], CustomSignatureFilter.Apply(signatures, "aftership").Select(s => s.Id));
        Assert.Equal([signature.Id], CustomSignatureFilter.Apply(signatures, "REF=AFTER").Select(s => s.Id));
        Assert.Equal([signature.Id], CustomSignatureFilter.Apply(signatures, "REFERRAL").Select(s => s.Id));
        Assert.Equal([signature.Id, other.Id], CustomSignatureFilter.Apply(signatures, null).Select(s => s.Id));
        Assert.Equal([signature.Id, other.Id], CustomSignatureFilter.Apply(signatures, "   ").Select(s => s.Id));
    }

    [Fact]
    public void ListFilterParser_rejects_invalid_enum_values()
    {
        Assert.Equal(IncidentState.Open, ListFilterParser.ParseEnum<IncidentState>("open"));
        Assert.Null(ListFilterParser.ParseEnum<IncidentState>("not-a-state"));
        Assert.Null(ListFilterParser.ParseEnum<IncidentState>("999"));
        Assert.Null(ListFilterParser.ParseEnum<IncidentState>("   "));
    }

    [Fact]
    public void ListSortParser_accepts_only_whitelisted_keys_and_directions()
    {
        Assert.Equal(
            new ListSort<EventSortColumn>(EventSortColumn.Source, SortDirection.Asc),
            ListSortParser.ParseEvent("source", "asc"));
        Assert.Equal(
            new ListSort<IncidentSortColumn>(IncidentSortColumn.Window, SortDirection.Desc),
            ListSortParser.ParseIncident("WINDOW", "DESC"));
        Assert.Equal(
            new ListSort<DecisionSortColumn>(DecisionSortColumn.Classification, SortDirection.Asc),
            ListSortParser.ParseDecision("classification", "asc"));
        Assert.Equal(
            new ListSort<AuditSortColumn>(AuditSortColumn.Timestamp, SortDirection.Desc),
            ListSortParser.ParseAudit("timestamp", "desc"));
        Assert.Equal(
            new ListSort<SignatureSortColumn>(SignatureSortColumn.Enabled, SortDirection.Desc),
            ListSortParser.ParseSignature("enabled", "desc"));

        Assert.Null(ListSortParser.ParseEvent("payload_json;drop table events", "asc"));
        Assert.Null(ListSortParser.ParseIncident("events", "asc"));
        Assert.Null(ListSortParser.ParseDecision("policy", "sideways"));
        Assert.Null(ListSortParser.ParseAudit("stage", null));
        Assert.Null(ListSortParser.ParseSignature(null, "asc"));
    }

    [Fact]
    public async Task In_memory_audit_filter_counts_and_preceding_across_pages()
    {
        var ledger = new InMemoryAuditLedger();
        var first = Audit("operator login accepted", PipelineStage.Admin);
        await Task.Delay(2);
        var excludedStage = Audit("operator login observed", PipelineStage.System);
        await Task.Delay(2);
        var second = Audit("operator LOGIN step-up", PipelineStage.Admin);
        await Task.Delay(2);
        var excludedText = Audit("configuration updated", PipelineStage.Admin);
        await Task.Delay(2);
        var third = Audit("operator login denied", PipelineStage.Admin);

        foreach (var record in new[] { first, excludedStage, second, excludedText, third })
        {
            await ledger.AppendAsync(record);
        }

        var filter = new AuditListFilter(" login ", PipelineStage.Admin);
        var page1 = await ledger.ListPageAsync(beforeId: null, pageSize: 2, filter: filter);
        var page2 = await ledger.ListPageAsync(page1.NextCursor, pageSize: 2, filter: filter);

        Assert.Equal([third.Id, second.Id], page1.Items.Select(i => i.Id));
        Assert.Equal(3, page1.TotalCount);
        Assert.Equal(0, page1.Preceding);
        Assert.Equal(first.Id, Assert.Single(page2.Items).Id);
        Assert.Equal(3, page2.TotalCount);
        Assert.Equal(2, page2.Preceding);
        Assert.Null(page2.NextCursor);
    }

    [Fact]
    public async Task In_memory_incident_filter_counts_and_preceding_across_pages()
    {
        var store = new InMemoryIncidentStore();
        var first = Incident("ip=198.51.100.10|window=60s", IncidentState.Open);
        await Task.Delay(2);
        var excludedState = Incident("ip=198.51.100.20|window=60s", IncidentState.Closed);
        await Task.Delay(2);
        var second = Incident("ip=198.51.100.30|window=60s", IncidentState.Open);
        await Task.Delay(2);
        var excludedText = Incident("ip=203.0.113.10|window=60s", IncidentState.Open);
        await Task.Delay(2);
        var third = Incident("ip=198.51.100.40|window=60s", IncidentState.Open);

        foreach (var incident in new[] { first, excludedState, second, excludedText, third })
        {
            await store.UpsertAsync(incident);
        }

        var filter = new IncidentListFilter("198.51.100", IncidentState.Open);
        var page1 = await store.ListPageAsync(beforeId: null, pageSize: 2, filter: filter);
        var page2 = await store.ListPageAsync(page1.NextCursor, pageSize: 2, filter: filter);

        Assert.Equal([third.Id, second.Id], page1.Items.Select(i => i.Id));
        Assert.Equal(3, page1.TotalCount);
        Assert.Equal(0, page1.Preceding);
        Assert.Equal(first.Id, Assert.Single(page2.Items).Id);
        Assert.Equal(3, page2.TotalCount);
        Assert.Equal(2, page2.Preceding);
        Assert.Null(page2.NextCursor);
    }

    private static CustomSignature Signature(string name, string pattern, string category) => new()
    {
        Id = ViegardId.New(),
        Name = name,
        Enabled = true,
        Target = CustomSignatureTarget.HttpUri,
        MatchType = CustomSignatureMatchType.Contains,
        Pattern = pattern,
        Category = category,
        Severity = 3,
        EvidenceWeight = 1.0,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        UpdatedBy = "test",
        Version = 1,
    };

    private static AuditRecord Audit(string summary, PipelineStage stage) => new()
    {
        Id = ViegardId.New(),
        Timestamp = DateTimeOffset.UtcNow,
        Stage = stage,
        Summary = summary,
    };

    private static Incident Incident(string correlationKey, IncidentState state) => new()
    {
        Id = ViegardId.New(),
        CorrelationKey = correlationKey,
        WindowStart = DateTimeOffset.UtcNow,
        WindowEnd = DateTimeOffset.UtcNow,
        EventIds = [],
        Evidence = [],
        State = state,
    };
}
