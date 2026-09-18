using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Viegard.AdminApi.Api;
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
