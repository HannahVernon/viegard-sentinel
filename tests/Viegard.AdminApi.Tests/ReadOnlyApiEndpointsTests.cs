using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Viegard.AdminApi.Api;
using Viegard.Application.Classifiers;
using Viegard.Application.Configuration;
using Viegard.Domain;
using Viegard.Domain.Classifications;
using Viegard.Domain.Decisions;
using Viegard.Domain.Events;
using Viegard.Persistence.InMemory;

namespace Viegard.AdminApi.Tests;

public sealed class ReadOnlyApiEndpointsTests
{
    [Fact]
    public async Task ListEvents_returns_page_with_polymorphic_payloads()
    {
        var events = new InMemoryEventStore();
        var first = Event("198.51.100.10");
        var second = Event("198.51.100.11");
        await events.AddAsync(first);
        await events.AddAsync(second);
        var context = Context("?take=1");

        var json = await ExecuteAsync(await ReadOnlyApiEndpoints.ListEventsAsync(context, events), context);

        Assert.Equal(1, json.GetProperty("items").GetArrayLength());
        Assert.Equal(2, json.GetProperty("totalCount").GetInt64());
        Assert.False(json.GetProperty("nextCursor").ValueKind is JsonValueKind.Null);
        var item = json.GetProperty("items")[0];
        Assert.Equal("http-request", item.GetProperty("payload").GetProperty("$payloadType").GetString());
        Assert.Equal("syslog:test", item.GetProperty("sourceId").GetString());
    }

    [Fact]
    public async Task ListDecisions_applies_outcome_severity_and_unreviewed_filters()
    {
        var classifications = new InMemoryClassificationStore();
        var decisions = new InMemoryDecisionStore(classifications);
        var low = await SeedDecisionAsync(decisions, classifications, severity: 2);
        await SeedDecisionAsync(decisions, classifications, severity: 8);
        var context = Context("?outcome=RequireApproval&maxSeverity=3&unreviewed=1");

        var json = await ExecuteAsync(await ReadOnlyApiEndpoints.ListDecisionsAsync(context, decisions), context);

        var items = json.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal(low.Id.ToString(), items[0].GetProperty("id").GetString());
        Assert.Equal("RequireApproval", items[0].GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task GetDecision_embeds_classification_and_404s_unknown_ids()
    {
        var classifications = new InMemoryClassificationStore();
        var decisions = new InMemoryDecisionStore(classifications);
        var decision = await SeedDecisionAsync(decisions, classifications, severity: 7);

        var context = Context(string.Empty);
        var json = await ExecuteAsync(
            await ReadOnlyApiEndpoints.GetDecisionAsync(decision.Id, decisions, classifications, context),
            context);
        Assert.Equal(decision.Id.ToString(), json.GetProperty("decision").GetProperty("id").GetString());
        Assert.Equal(7, json.GetProperty("classification").GetProperty("severity").GetInt32());

        var missingContext = Context(string.Empty);
        var missing = await ReadOnlyApiEndpoints.GetDecisionAsync(
            ViegardId.New(), decisions, classifications, missingContext);
        await missing.ExecuteAsync(missingContext);
        Assert.Equal(StatusCodes.Status404NotFound, missingContext.Response.StatusCode);
    }

    [Fact]
    public async Task GetAdvisorSummary_reports_configuration_and_outcome_windows()
    {
        var consults = new InMemoryLocalModelAdvisorConsultStore();
        var now = DateTimeOffset.UtcNow;
        await consults.AppendAsync(Consult(AdvisorConsultOutcome.Escalated, now.AddMinutes(-5), 100));
        await consults.AppendAsync(Consult(AdvisorConsultOutcome.DeEscalated, now.AddMinutes(-5), 150));
        await consults.AppendAsync(Consult(AdvisorConsultOutcome.NoChange, now.AddMinutes(-6), 200, servedFromCache: true, injectionDetected: true, advisorSkippedForInjection: true));
        await consults.AppendAsync(Consult(AdvisorConsultOutcome.ProviderFailed, now.AddMinutes(-7), 400, "Timeout"));
        var settings = new InMemoryLocalModelAdvisorSettingsStore();
        await settings.UpsertAsync(
            new LocalModelAdvisorSettings
            {
                Enabled = true,
                Endpoint = "http://example.test:11434",
                Model = "qwen-test:latest",
                Temperature = 0.1,
                TimeoutMs = 9000,
                InvokeConfidenceMin = 0.40,
                InvokeConfidenceMax = 0.80,
                MaxSeverityDelta = 2,
                MaxConfidenceDelta = 0.15,
                ResponseCacheEnabled = true,
                ResponseCacheTtlHours = 48,
                EnsembleEnabled = true,
                SecondModelEndpoint = "http://second.example:11434",
                SecondModel = "qwen-second:latest",
                InjectionAction = AdvisorInjectionAction.RecordOnly,
                DeEscalationEnabled = true,
                MaxDownwardSeverityDelta = 2,
                MaxDownwardConfidenceDelta = 0.10,
                DeEscalationMinModelConfidence = 0.7,
                DeEscalationProtectedSeverity = 7,
            },
            expectedVersion: 0,
            updatedBy: "tester",
            updatedAt: now);
        var promptTemplates = new InMemoryLocalModelAdvisorPromptTemplateStore();
        var activePrompt = await promptTemplates.CreateRevisionAsync(
            AdvisoryIncidentClassifier.PromptTemplateId,
            LocalModelAdvisorPrompt.Template.SystemInstructions,
            LocalModelAdvisorPrompt.Template.ApplicationInstructions,
            "test",
            "tester",
            now);
        var categoryBands = new InMemoryLocalModelAdvisorCategoryBandStore();
        await categoryBands.UpsertAsync(
            new LocalModelAdvisorCategoryBand
            {
                Category = "path-traversal",
                Enabled = false,
                InvokeConfidenceMin = null,
                InvokeConfidenceMax = 0.75,
                MaxSeverityDelta = 1,
                MaxConfidenceDelta = null,
                DeEscalationEnabled = true,
                MaxDownwardSeverityDelta = 2,
                MaxDownwardConfidenceDelta = 0.1,
            },
            expectedVersion: 0,
            updatedBy: "tester",
            updatedAt: now);
        var context = Context(string.Empty);

        var json = await ExecuteAsync(
            await ReadOnlyApiEndpoints.GetAdvisorSummaryAsync(context, consults, settings, categoryBands, promptTemplates),
            context);

        var config = json.GetProperty("configuration");
        Assert.True(config.GetProperty("enabled").GetBoolean());
        Assert.Equal("http://example.test:11434", config.GetProperty("endpoint").GetString());
        Assert.Equal("qwen-test:latest", config.GetProperty("model").GetString());
        Assert.Equal(0.1, config.GetProperty("temperature").GetDouble());
        Assert.Equal(9000, config.GetProperty("timeoutMs").GetInt32());
        Assert.Equal(0.40, config.GetProperty("invokeConfidenceMin").GetDouble());
        Assert.Equal(0.80, config.GetProperty("invokeConfidenceMax").GetDouble());
        Assert.Equal(2, config.GetProperty("maxSeverityDelta").GetInt32());
        Assert.Equal(0.15, config.GetProperty("maxConfidenceDelta").GetDouble());
        Assert.True(config.GetProperty("responseCacheEnabled").GetBoolean());
        Assert.Equal(48, config.GetProperty("responseCacheTtlHours").GetInt32());
        Assert.True(config.GetProperty("ensembleEnabled").GetBoolean());
        Assert.Equal("http://second.example:11434", config.GetProperty("secondModelEndpoint").GetString());
        Assert.Equal("qwen-second:latest", config.GetProperty("secondModel").GetString());
        Assert.Equal("RecordOnly", config.GetProperty("injectionAction").GetString());
        Assert.True(config.GetProperty("deEscalationEnabled").GetBoolean());
        Assert.Equal(2, config.GetProperty("maxDownwardSeverityDelta").GetInt32());
        Assert.Equal(0.10, config.GetProperty("maxDownwardConfidenceDelta").GetDouble());
        Assert.Equal(0.7, config.GetProperty("deEscalationMinModelConfidence").GetDouble());
        Assert.Equal(7, config.GetProperty("deEscalationProtectedSeverity").GetInt32());
        Assert.Equal(1, config.GetProperty("version").GetInt32());

        var activePromptTemplate = json.GetProperty("activePromptTemplate");
        Assert.Equal(AdvisoryIncidentClassifier.PromptTemplateId, activePromptTemplate.GetProperty("templateId").GetString());
        Assert.Equal(activePrompt.Revision, activePromptTemplate.GetProperty("revision").GetInt32());

        var categoryOverride = Assert.Single(json.GetProperty("categoryOverrides").EnumerateArray());
        Assert.Equal("path-traversal", categoryOverride.GetProperty("category").GetString());
        Assert.False(categoryOverride.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, categoryOverride.GetProperty("invokeConfidenceMin").ValueKind);
        Assert.Equal(0.75, categoryOverride.GetProperty("invokeConfidenceMax").GetDouble());
        Assert.Equal(1, categoryOverride.GetProperty("maxSeverityDelta").GetInt32());
        Assert.Equal(JsonValueKind.Null, categoryOverride.GetProperty("maxConfidenceDelta").ValueKind);
        Assert.True(categoryOverride.GetProperty("deEscalationEnabled").GetBoolean());
        Assert.Equal(2, categoryOverride.GetProperty("maxDownwardSeverityDelta").GetInt32());
        Assert.Equal(0.1, categoryOverride.GetProperty("maxDownwardConfidenceDelta").GetDouble());
        Assert.Equal(1, categoryOverride.GetProperty("version").GetInt32());

        var oneHour = json.GetProperty("windows").EnumerateArray().Single(w => w.GetProperty("window").GetString() == "1h");
        Assert.Equal(1, oneHour.GetProperty("escalated").GetInt64());
        Assert.Equal(1, oneHour.GetProperty("deEscalated").GetInt64());
        Assert.Equal(1, oneHour.GetProperty("noChange").GetInt64());
        Assert.Equal(1, oneHour.GetProperty("providerFailed").GetInt64());
        Assert.Equal(1, oneHour.GetProperty("failures").GetInt64());
        Assert.Equal(1, oneHour.GetProperty("cacheHits").GetInt64());
        Assert.Equal(1, oneHour.GetProperty("injectionDetected").GetInt64());
        Assert.Equal(1, oneHour.GetProperty("skippedForInjection").GetInt64());
        Assert.Equal(4, oneHour.GetProperty("total").GetInt64());
        Assert.Equal(1.0 / 3.0, oneHour.GetProperty("escalationRate").GetDouble(), precision: 10);
        Assert.Equal(1.0 / 3.0, oneHour.GetProperty("deEscalationRate").GetDouble(), precision: 10);
        Assert.Equal(3, oneHour.GetProperty("latency").GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task ListAdvisorConsults_pages_and_filters_by_outcome()
    {
        var consults = new InMemoryLocalModelAdvisorConsultStore();
        var now = DateTimeOffset.UtcNow;
        await consults.AppendAsync(Consult(AdvisorConsultOutcome.Escalated, now.AddMinutes(-1), 100));
        await consults.AppendAsync(Consult(AdvisorConsultOutcome.NoChange, now.AddMinutes(-2), 200));
        await consults.AppendAsync(Consult(AdvisorConsultOutcome.NoChange, now.AddMinutes(-3), 300));

        var pageContext = Context("?take=1");
        var page = await ExecuteAsync(await ReadOnlyApiEndpoints.ListAdvisorConsultsAsync(pageContext, consults), pageContext);
        Assert.Equal(1, page.GetProperty("items").GetArrayLength());
        Assert.Equal(3, page.GetProperty("totalCount").GetInt64());
        Assert.False(page.GetProperty("nextCursor").ValueKind is JsonValueKind.Null);

        var filterContext = Context("?outcome=NoChange");
        var filtered = await ExecuteAsync(await ReadOnlyApiEndpoints.ListAdvisorConsultsAsync(filterContext, consults), filterContext);
        Assert.Equal(2, filtered.GetProperty("items").GetArrayLength());
        Assert.Equal(2, filtered.GetProperty("totalCount").GetInt64());
        foreach (var item in filtered.GetProperty("items").EnumerateArray())
        {
            Assert.Equal("NoChange", item.GetProperty("outcome").GetString());
        }
    }

    [Fact]
    public async Task GetAdvisorConsult_returns_record_by_id_and_by_classification_and_404s()
    {
        var consults = new InMemoryLocalModelAdvisorConsultStore();
        var record = Consult(AdvisorConsultOutcome.Escalated, DateTimeOffset.UtcNow.AddMinutes(-1), 150) with
        {
            EnsembleDetail = new AdvisorEnsembleDetail(
                "average",
                [
                    new AdvisorEnsembleModelOutput("qwen-primary:latest", "http://primary.example", 7, 0.7, true),
                    new AdvisorEnsembleModelOutput("qwen-second:latest", "http://second.example", 9, 0.9, true),
                ]),
        };
        await consults.AppendAsync(record);

        var byIdContext = Context(string.Empty);
        var byId = await ExecuteAsync(
            await ReadOnlyApiEndpoints.GetAdvisorConsultAsync(record.Id, consults, byIdContext),
            byIdContext);
        Assert.Equal(record.Id.ToString(), byId.GetProperty("id").GetString());
        Assert.Equal("Escalated", byId.GetProperty("outcome").GetString());
        var detail = byId.GetProperty("ensembleDetail");
        Assert.Equal("average", detail.GetProperty("rule").GetString());
        Assert.Equal(2, detail.GetProperty("models").GetArrayLength());

        var byClassContext = Context(string.Empty);
        var byClass = await ExecuteAsync(
            await ReadOnlyApiEndpoints.GetAdvisorConsultByClassificationAsync(record.ClassificationId, consults, byClassContext),
            byClassContext);
        Assert.Equal(record.Id.ToString(), byClass.GetProperty("id").GetString());

        var missingContext = Context(string.Empty);
        var missing = await ReadOnlyApiEndpoints.GetAdvisorConsultAsync(ViegardId.New(), consults, missingContext);
        await missing.ExecuteAsync(missingContext);
        Assert.Equal(StatusCodes.Status404NotFound, missingContext.Response.StatusCode);
    }

    private static AdvisorConsultRecord Consult(
        AdvisorConsultOutcome outcome,
        DateTimeOffset createdAt,
        int? latencyMs,
        string? failureKind = null,
        bool servedFromCache = false,
        bool injectionDetected = false,
        bool advisorSkippedForInjection = false) => new()
    {
        ClassificationId = ViegardId.New(),
        IncidentId = ViegardId.New(),
        Category = "scanner",
        Outcome = outcome,
        BaseSeverity = 6,
        FinalSeverity = outcome == AdvisorConsultOutcome.Escalated ? 8 : outcome == AdvisorConsultOutcome.DeEscalated ? 4 : 6,
        BaseConfidence = 0.6,
        FinalConfidence = outcome == AdvisorConsultOutcome.Escalated ? 0.8 : outcome == AdvisorConsultOutcome.DeEscalated ? 0.5 : 0.6,
        LatencyMs = latencyMs,
        FailureKind = failureKind,
        ModelId = "qwen-test:latest",
        ServedFromCache = servedFromCache,
        InjectionDetected = injectionDetected,
        InjectionCategories = injectionDetected ? [nameof(AdvisorInjectionPatternCategory.OutputControlHijack)] : [],
        AdvisorSkippedForInjection = advisorSkippedForInjection,
        CreatedAt = createdAt,
    };

    private static DefaultHttpContext Context(string queryString)
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = HttpMethods.Get;
        if (queryString.Length > 0)
        {
            context.Request.QueryString = new QueryString(queryString);
        }

        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<JsonElement> ExecuteAsync(IResult result, DefaultHttpContext context)
    {
        await result.ExecuteAsync(context);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.Clone();
    }

    private static NormalizedEvent Event(string ip) => new()
    {
        Id = ViegardId.New(),
        SourceId = "syslog:test",
        SourceType = "syslog",
        OccurredAt = DateTimeOffset.UtcNow,
        Entities = [new EntityRef { Kind = EntityKind.IpAddress, Value = ip }],
        Payload = new HttpRequestEvent
        {
            RemoteAddress = ip,
            RequestedAt = DateTimeOffset.UtcNow,
            Method = "GET",
            Uri = "/probe",
            Protocol = "HTTP/1.1",
            StatusCode = 404,
            BodyBytes = 0,
        },
        RawObservationId = ViegardId.New(),
    };

    private static async Task<Decision> SeedDecisionAsync(
        InMemoryDecisionStore decisions,
        InMemoryClassificationStore classifications,
        int severity)
    {
        var classification = new Classification
        {
            Id = ViegardId.New(),
            SubjectKind = ClassificationSubjectKind.Incident,
            SubjectId = ViegardId.New(),
            ClassifierId = "test-classifier",
            Category = "scanner",
            Confidence = 0.5,
            Severity = severity,
            Reasons = ["test"],
            RecommendedAction = "temp-ban-ip",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await classifications.AddAsync(classification);
        var decision = new Decision
        {
            Id = ViegardId.New(),
            ClassificationId = classification.Id,
            PolicyId = "viegard-default",
            PolicyVersion = "1",
            Outcome = DecisionOutcome.RequireApproval,
            Rationale = "requires approval",
            Guardrails = [],
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await decisions.AddAsync(decision);
        return decision;
    }
}
