using Viegard.Application.Stores;
using Viegard.Domain;
using Viegard.Domain.Audit;
using Viegard.Domain.Configuration;
using Viegard.Domain.Decisions;
using Viegard.Domain.Events;
using Viegard.Domain.Incidents;
using Viegard.Persistence.InMemory;

namespace Viegard.Application.Tests;

public sealed class ListFilterTests
{
    [Fact]
    public void SearchQuery_parse_ands_terms_inside_or_groups()
    {
        var query = SearchQuery.Parse("a b OR c");

        Assert.Collection(
            query.Groups,
            group =>
            {
                Assert.Equal(["a", "b"], group.Include);
                Assert.Empty(group.Exclude);
            },
            group =>
            {
                Assert.Equal(["c"], group.Include);
                Assert.Empty(group.Exclude);
            });
    }

    [Fact]
    public void SearchQuery_parse_keeps_quoted_phrases_and_negated_terms()
    {
        var query = SearchQuery.Parse("""alpha -"needs example.com" NOT beta""");

        var group = Assert.Single(query.Groups);
        Assert.Equal(["alpha"], group.Include);
        Assert.Equal(["needs example.com", "beta"], group.Exclude);
    }

    [Fact]
    public void SearchQuery_parse_caps_terms_and_keeps_escaping_literal()
    {
        var query = SearchQuery.Parse(@"a\b%c_d one two three four five six seven eight");

        var group = Assert.Single(query.Groups);
        Assert.Equal(
            [@"a\b%c_d", "one", "two", "three", "four", "five", "six", "seven"],
            group.Include);
        Assert.Equal(SearchQuery.MaxTerms, group.Include.Count);
    }

    [Fact]
    public void SearchQuery_parse_drops_empty_terms_and_dangling_operators()
    {
        var query = SearchQuery.Parse("""OR "" NOT "" alpha OR NOT OR beta NOT""");

        Assert.Collection(
            query.Groups,
            group => Assert.Equal(["alpha"], group.Include),
            group => Assert.Equal(["beta"], group.Include));
        Assert.All(query.Groups, group => Assert.Empty(group.Exclude));
    }

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
    public void CustomSignatureFilter_applies_boolean_groups_and_negation()
    {
        var alpha = Signature(name: "alpha bot", pattern: "/admin", category: "scanner");
        var beta = Signature(name: "beta bot", pattern: "/login", category: "scanner");
        var blocked = Signature(name: "beta blocked", pattern: "/login", category: "scanner");
        var other = Signature(name: "gamma", pattern: "/health", category: "operator");
        var signatures = new[] { alpha, beta, blocked, other };

        var matches = CustomSignatureFilter.Apply(signatures, "alpha OR beta -blocked").Select(s => s.Id);

        Assert.Equal([alpha.Id, beta.Id], matches);
    }

    [Fact]
    public async Task In_memory_event_filter_applies_boolean_query_with_sort()
    {
        var store = new InMemoryEventStore();
        var alpha = Event("it-filter-alpha", "/admin");
        var beta = Event("it-filter-beta", "/login");
        var blocked = Event("it-filter-beta-blocked", "/login");
        var other = Event("it-filter-gamma", "/health");
        foreach (var item in new[] { other, blocked, beta, alpha })
        {
            await store.AddAsync(item);
        }

        var page = await store.ListPageAsync(
            beforeId: null,
            pageSize: 10,
            filter: new EventListFilter("alpha OR beta -blocked"),
            sort: new ListSort<EventSortColumn>(EventSortColumn.Source, SortDirection.Asc));

        Assert.Equal([alpha.Id, beta.Id], page.Items.Select(e => e.Id));
    }

    [Fact]
    public async Task In_memory_incident_filter_combines_boolean_query_state_sort_and_page_jump()
    {
        var store = new InMemoryIncidentStore();
        var alpha = Incident("it-filter alpha open", IncidentState.Open);
        var beta = Incident("it-filter beta open", IncidentState.Open);
        var blocked = Incident("it-filter beta blocked", IncidentState.Open);
        var closed = Incident("it-filter alpha closed", IncidentState.Closed);
        foreach (var item in new[] { blocked, beta, closed, alpha })
        {
            await store.UpsertAsync(item);
        }

        var filter = new IncidentListFilter("alpha OR beta -blocked", IncidentState.Open);
        var sort = new ListSort<IncidentSortColumn>(IncidentSortColumn.CorrelationKey, SortDirection.Asc);
        var page1 = await store.ListPageAsync(beforeId: null, pageSize: 1, filter: filter, sort: sort);
        var page2 = await store.ListPageAsync(page1.NextCursor, pageSize: 1, filter: filter, sort: sort);
        var jumpCursor = await store.GetPageCursorAsync(pageNumber: 2, pageSize: 1, filter: filter, sort: sort);
        var jumpPage = await store.ListPageAsync(jumpCursor, pageSize: 1, filter: filter, sort: sort);
        var clampedCursor = await store.GetPageCursorAsync(pageNumber: 99, pageSize: 1, filter: filter, sort: sort);
        var clampedPage = await store.ListPageAsync(clampedCursor, pageSize: 1, filter: filter, sort: sort);

        Assert.Equal(alpha.Id, Assert.Single(page1.Items).Id);
        Assert.Equal(beta.Id, Assert.Single(page2.Items).Id);
        Assert.Equal(page2.Items.Select(i => i.Id), jumpPage.Items.Select(i => i.Id));
        Assert.Equal(page2.Items.Select(i => i.Id), clampedPage.Items.Select(i => i.Id));
        Assert.Null(await store.GetPageCursorAsync(pageNumber: -5, pageSize: 1, filter: filter, sort: sort));
    }

    [Fact]
    public async Task In_memory_decision_filter_combines_boolean_query_and_outcome()
    {
        var store = new InMemoryDecisionStore();
        var alpha = Decision("it-filter alpha approved", DecisionOutcome.Permit);
        var beta = Decision("it-filter beta approved", DecisionOutcome.Permit);
        var blocked = Decision("it-filter beta blocked", DecisionOutcome.Permit);
        var dryRun = Decision("it-filter alpha dry-run", DecisionOutcome.DryRun);
        foreach (var item in new[] { dryRun, blocked, beta, alpha })
        {
            await store.AddAsync(item);
        }

        var page = await store.ListPageAsync(
            beforeId: null,
            pageSize: 10,
            filter: new DecisionListFilter("alpha OR beta -blocked", DecisionOutcome.Permit),
            sort: new ListSort<DecisionSortColumn>(DecisionSortColumn.Created, SortDirection.Asc));

        Assert.Equal([alpha.Id, beta.Id], page.Items.Select(d => d.Id));
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

    private static Decision Decision(string rationale, DecisionOutcome outcome) => new()
    {
        Id = ViegardId.New(),
        ClassificationId = ViegardId.New(),
        PolicyId = "test-policy",
        PolicyVersion = "1",
        Outcome = outcome,
        Rationale = rationale,
        Guardrails = [],
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static NormalizedEvent Event(string sourceId, string uri) => new()
    {
        Id = ViegardId.New(),
        SourceId = sourceId,
        SourceType = "syslog",
        OccurredAt = DateTimeOffset.UtcNow,
        Entities = [],
        Payload = new HttpRequestEvent
        {
            RemoteAddress = "203.0.113.10",
            Method = "GET",
            Uri = uri,
            Protocol = "HTTP/1.1",
            StatusCode = 200,
            Host = "www.example.com",
        },
        RawObservationId = ViegardId.New(),
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
