using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Viegard.AdminApi.Auth;
using Viegard.AdminApi.Errors;
using Viegard.Application.Audit;
using Viegard.Application.Classifiers;
using Viegard.Application.Configuration;
using Viegard.Application.Policy;
using Viegard.Application.Stores;
using Viegard.Application.Telemetry;
using Viegard.Domain.Audit;
using Viegard.Domain.Decisions;
using Viegard.Domain.Incidents;

namespace Viegard.AdminApi.Api;

/// <summary>
/// Read-only JSON API over the list stores (D-0040 scope b).  Every endpoint
/// is GET-only, opted into the read-only-api policy (cookie session or app
/// password), and serializes domain records directly with ISO-8601
/// timestamps and string enums for machine consumption.  Text searches share
/// the interactive pages' command budget and answer 408 when it elapses.
/// </summary>
public static class ReadOnlyApiEndpoints
{
    private const int DefaultTake = 50;
    private const int MaxTake = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static void MapReadOnlyApiEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization(AppPasswordDefaults.ReadOnlyApiPolicy);
        api.MapGet("/events", ListEventsAsync);
        api.MapGet("/events/{id:guid}", GetEventAsync);
        api.MapGet("/incidents", ListIncidentsAsync);
        api.MapGet("/incidents/{id:guid}", GetIncidentAsync);
        api.MapGet("/decisions", ListDecisionsAsync);
        api.MapGet("/decisions/{id:guid}", GetDecisionAsync);
        api.MapGet("/classifications/{id:guid}", GetClassificationAsync);
        api.MapGet("/classifier/settings", GetClassifierSettingsAsync);
        api.MapGet("/policy/thresholds", GetPolicyThresholdsAsync);
        api.MapGet("/advisor/summary", GetAdvisorSummaryAsync);
        api.MapGet("/advisor/consults", ListAdvisorConsultsAsync);
        api.MapGet("/advisor/consults/by-classification/{id:guid}", GetAdvisorConsultByClassificationAsync);
        api.MapGet("/advisor/consults/{id:guid}", GetAdvisorConsultAsync);
        api.MapGet("/audit", ListAuditAsync);
        api.MapGet("/bans", ListBansAsync);
        api.MapGet("/instances", GetInstancesAsync);
    }

    internal static async Task<IResult> ListEventsAsync(
        HttpContext context,
        IEventStore events)
    {
        var query = ReadListQuery(context);
        return await ListAsync(query, (cursor, take) => events.ListPageAsync(
            cursor,
            take,
            new EventListFilter(query.Text),
            ListSortParser.ParseEvent(query.Sort, query.Direction),
            context.RequestAborted)).ConfigureAwait(false);
    }

    internal static async Task<IResult> ListIncidentsAsync(
        HttpContext context,
        IIncidentStore incidents)
    {
        var query = ReadListQuery(context);
        var state = ListFilterParser.ParseEnum<IncidentState>(context.Request.Query["state"]);
        return await ListAsync(query, (cursor, take) => incidents.ListPageAsync(
            cursor,
            take,
            new IncidentListFilter(query.Text, state),
            ListSortParser.ParseIncident(query.Sort, query.Direction),
            context.RequestAborted)).ConfigureAwait(false);
    }

    internal static async Task<IResult> ListDecisionsAsync(
        HttpContext context,
        IDecisionStore decisions)
    {
        var query = ReadListQuery(context);
        var request = context.Request.Query;
        var outcome = ListFilterParser.ParseEnum<DecisionOutcome>(request["outcome"]);
        var filter = new DecisionListFilter(
            query.Text,
            outcome,
            ParseSeverity(request["minSeverity"]),
            ParseSeverity(request["maxSeverity"]),
            request["unreviewed"].ToString() is "1" or "true");
        return await ListAsync(query, (cursor, take) => decisions.ListPageAsync(
            cursor,
            take,
            filter,
            ListSortParser.ParseDecision(query.Sort, query.Direction),
            context.RequestAborted)).ConfigureAwait(false);
    }

    internal static async Task<IResult> ListAuditAsync(
        HttpContext context,
        IAuditLedger audit)
    {
        var query = ReadListQuery(context);
        var stage = ListFilterParser.ParseEnum<PipelineStage>(context.Request.Query["stage"]);
        return await ListAsync(query, (cursor, take) => audit.ListPageAsync(
            cursor,
            take,
            new AuditListFilter(query.Text, stage),
            ListSortParser.ParseAudit(query.Sort, query.Direction),
            context.RequestAborted)).ConfigureAwait(false);
    }

    internal static async Task<IResult> ListBansAsync(
        HttpContext context,
        Viegard.Application.Actions.IActiveBanStore activeBans,
        IActionStore actions)
    {
        var now = DateTimeOffset.UtcNow;
        var bans = await activeBans.ListUnexpiredAsync(now, context.RequestAborted).ConfigureAwait(false);
        var recent = await actions.ListRecentByProviderAsync(
            Viegard.Actions.MikroTik.MikroTikBanActionProvider.MikroTikProviderId,
            50,
            context.RequestAborted).ConfigureAwait(false);
        return Results.Json(new { activeBans = bans, recentActions = recent }, Json);
    }

    internal static async Task<IResult> GetEventAsync(Guid id, IEventStore events, HttpContext context)
    {
        var found = await events.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
        return found is null ? NotFound() : Results.Json(found, Json);
    }

    internal static async Task<IResult> GetIncidentAsync(Guid id, IIncidentStore incidents, HttpContext context)
    {
        var found = await incidents.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
        return found is null ? NotFound() : Results.Json(found, Json);
    }

    internal static async Task<IResult> GetDecisionAsync(
        Guid id,
        IDecisionStore decisions,
        IClassificationStore classifications,
        HttpContext context)
    {
        var decision = await decisions.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
        if (decision is null)
        {
            return NotFound();
        }

        var classification = await classifications.GetAsync(decision.ClassificationId, context.RequestAborted)
            .ConfigureAwait(false);
        return Results.Json(new { decision, classification }, Json);
    }

    internal static async Task<IResult> GetClassificationAsync(
        Guid id,
        IClassificationStore classifications,
        HttpContext context)
    {
        var found = await classifications.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
        return found is null ? NotFound() : Results.Json(found, Json);
    }

    internal static async Task<IResult> GetAdvisorSummaryAsync(
        HttpContext context,
        ILocalModelAdvisorConsultStore consults,
        ILocalModelAdvisorSettingsStore settingsStore,
        ILocalModelAdvisorCategoryBandStore categoryBands,
        ILocalModelAdvisorPromptTemplateStore promptTemplates)
    {
        var settings = await settingsStore.GetAsync(context.RequestAborted).ConfigureAwait(false);
        var bands = await categoryBands.ListAsync(context.RequestAborted).ConfigureAwait(false);
        var activePromptRevision = await promptTemplates.GetActiveAsync(Viegard.Application.Classifiers.AdvisoryIncidentClassifier.PromptTemplateId, context.RequestAborted).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var definitions = new (string Label, DateTimeOffset Since)[]
        {
            ("1h", now.AddHours(-1)),
            ("24h", now.AddDays(-1)),
            ("7d", now.AddDays(-7)),
            ("all", DateTimeOffset.UnixEpoch),
        };

        var windows = new List<object>(definitions.Length);
        foreach (var (label, since) in definitions)
        {
            var counts = await consults.GetOutcomeCountsAsync(since, context.RequestAborted).ConfigureAwait(false);
            var latency = await consults.GetLatencyStatsAsync(since, context.RequestAborted).ConfigureAwait(false);
            var cacheHits = await consults.GetCacheHitCountAsync(since, context.RequestAborted).ConfigureAwait(false);
            var injectionDetected = await consults.GetInjectionDetectedCountAsync(since, context.RequestAborted).ConfigureAwait(false);
            var skippedForInjection = await consults.GetSkippedForInjectionCountAsync(since, context.RequestAborted).ConfigureAwait(false);
            var byOutcome = counts.ToDictionary(c => c.Outcome, c => c.Count);
            long Count(AdvisorConsultOutcome outcome) => byOutcome.GetValueOrDefault(outcome);
            var escalated = Count(AdvisorConsultOutcome.Escalated);
            var deEscalated = Count(AdvisorConsultOutcome.DeEscalated);
            var noChange = Count(AdvisorConsultOutcome.NoChange);
            var considered = escalated + deEscalated + noChange;
            windows.Add(new
            {
                window = label,
                since,
                escalated,
                deEscalated,
                noChange,
                providerFailed = Count(AdvisorConsultOutcome.ProviderFailed),
                invalidOutput = Count(AdvisorConsultOutcome.InvalidOutput),
                skippedOutOfBand = Count(AdvisorConsultOutcome.SkippedOutOfBand),
                cacheHits,
                injectionDetected,
                skippedForInjection,
                total = byOutcome.Values.Sum(),
                failures = Count(AdvisorConsultOutcome.ProviderFailed) + Count(AdvisorConsultOutcome.InvalidOutput),
                escalationRate = considered == 0 ? (double?)null : escalated / (double)considered,
                deEscalationRate = considered == 0 ? (double?)null : deEscalated / (double)considered,
                latency = new { count = latency.Count, p50Ms = latency.P50, p95Ms = latency.P95 },
            });
        }

        var effective = settings ?? new LocalModelAdvisorSettings();
        var configuration = new
        {
            enabled = effective.Enabled,
            endpoint = effective.Endpoint,
            model = effective.Model,
            temperature = effective.Temperature,
            timeoutMs = effective.TimeoutMs,
            keepAlive = effective.KeepAlive,
            invokeConfidenceMin = effective.InvokeConfidenceMin,
            invokeConfidenceMax = effective.InvokeConfidenceMax,
            maxSeverityDelta = effective.MaxSeverityDelta,
            maxConfidenceDelta = effective.MaxConfidenceDelta,
            responseCacheEnabled = effective.ResponseCacheEnabled,
            responseCacheTtlHours = effective.ResponseCacheTtlHours,
            ensembleEnabled = effective.EnsembleEnabled,
            secondModelEndpoint = effective.SecondModelEndpoint,
            secondModel = effective.SecondModel,
            injectionAction = effective.InjectionAction.ToString(),
            deEscalationEnabled = effective.DeEscalationEnabled,
            maxDownwardSeverityDelta = effective.MaxDownwardSeverityDelta,
            maxDownwardConfidenceDelta = effective.MaxDownwardConfidenceDelta,
            deEscalationMinModelConfidence = effective.DeEscalationMinModelConfidence,
            deEscalationProtectedSeverity = effective.DeEscalationProtectedSeverity,
            version = effective.Version,
            updatedAt = effective.UpdatedAt,
        };
        var categoryOverrides = bands.Select(band => new
        {
            category = band.Category,
            enabled = band.Enabled,
            invokeConfidenceMin = band.InvokeConfidenceMin,
            invokeConfidenceMax = band.InvokeConfidenceMax,
            maxSeverityDelta = band.MaxSeverityDelta,
            maxConfidenceDelta = band.MaxConfidenceDelta,
            deEscalationEnabled = band.DeEscalationEnabled,
            maxDownwardSeverityDelta = band.MaxDownwardSeverityDelta,
            maxDownwardConfidenceDelta = band.MaxDownwardConfidenceDelta,
            version = band.Version,
            updatedAt = band.UpdatedAt,
        }).ToList();
        var activePromptTemplate = activePromptRevision is null
            ? null
            : new
            {
                templateId = activePromptRevision.TemplateId,
                revision = activePromptRevision.Revision,
                createdAt = activePromptRevision.CreatedAt,
            };
        return Results.Json(new { configuration, categoryOverrides, activePromptTemplate, windows }, Json);
    }

    internal static async Task<IResult> GetClassifierSettingsAsync(
        HttpContext context,
        IClassifierSettingsStore settingsStore,
        ClassifierSettingsSource source,
        IOptions<ClassifierOptions> options)
    {
        var settings = await settingsStore.GetAsync(context.RequestAborted).ConfigureAwait(false);
        var values = settings?.ToValues() ?? source.CurrentValues(options.Value);
        return Results.Json(new
        {
            scoreForFullConfidence = values.ScoreForFullConfidence,
            severityPerScorePoint = values.SeverityPerScorePoint,
            blockRecommendationScore = values.BlockRecommendationScore,
            repeatConfidenceMinEvents = values.RepeatConfidenceMinEvents,
            repeatConfidenceCoefficient = values.RepeatConfidenceCoefficient,
            repeatConfidenceBonusCap = values.RepeatConfidenceBonusCap,
            version = settings?.RowVersion ?? source.Current.RowVersion,
            updatedAt = settings?.UpdatedAt,
            updatedBy = settings?.UpdatedBy,
            seeded = settings is not null || source.Current.IsSeeded,
        }, Json);
    }

    internal static async Task<IResult> GetPolicyThresholdsAsync(
        HttpContext context,
        IPolicyThresholdSettingsStore settingsStore,
        PolicyThresholdSource source,
        IOptions<PolicyOptions> options)
    {
        var settings = await settingsStore.GetAsync(context.RequestAborted).ConfigureAwait(false);
        var values = settings?.ToValues() ?? source.CurrentValues(options.Value);
        return Results.Json(new
        {
            reviewConfidence = values.ReviewConfidence,
            actionConfidence = values.ActionConfidence,
            actionMinSeverity = values.ActionMinSeverity,
            version = settings?.RowVersion ?? source.Current.RowVersion,
            updatedAt = settings?.UpdatedAt,
            updatedBy = settings?.UpdatedBy,
            seeded = settings is not null || source.Current.IsSeeded,
        }, Json);
    }

    internal static async Task<IResult> GetInstancesAsync(
        HttpContext context,
        IInstanceRegistryStore instances,
        IQueueTelemetryStore queueTelemetry)
    {
        var now = DateTimeOffset.UtcNow;
        var registrations = await instances.ListAsync(context.RequestAborted).ConfigureAwait(false);
        var snapshots = await queueTelemetry.GetLatestAsync(context.RequestAborted).ConfigureAwait(false);
        var view = QueueStatusView.Build(snapshots, registrations, now);
        var rows = view.Instances.Select(row => new
        {
            instanceId = row.InstanceId,
            version = row.Registration?.Version,
            commitSha = row.Registration?.CommitSha,
            shortCommit = row.VersionLabel,
            roles = row.Registration?.Roles,
            hostName = row.Registration?.HostName,
            upgradeTarget = row.Registration?.UpgradeTarget,
            startedAt = row.StartedAt,
            reportedAt = row.Registration?.ReportedAt,
            queuesReported = row.QueueNames,
            lastCapturedAt = row.LastCapturedAt,
            stalenessLight = row.Light,
        }).ToList();
        return Results.Json(new { generatedAt = now, worstLight = view.WorstLight, instances = rows }, Json);
    }

    internal static async Task<IResult> ListAdvisorConsultsAsync(
        HttpContext context,
        ILocalModelAdvisorConsultStore consults)
    {
        var request = context.Request.Query;
        var take = int.TryParse(request["take"], out var parsedTake)
            ? Math.Clamp(parsedTake, 1, MaxTake)
            : DefaultTake;
        Guid? cursor = Guid.TryParse(request["cursor"], out var parsedCursor) ? parsedCursor : null;
        var outcome = ListFilterParser.ParseEnum<AdvisorConsultOutcome>(request["outcome"]);
        var page = await consults.ListPageAsync(cursor, take, outcome, context.RequestAborted).ConfigureAwait(false);
        return Results.Json(new
        {
            items = page.Items,
            nextCursor = page.NextCursor,
            totalCount = page.TotalCount,
            preceding = page.Preceding,
        }, Json);
    }

    internal static async Task<IResult> GetAdvisorConsultAsync(
        Guid id,
        ILocalModelAdvisorConsultStore consults,
        HttpContext context)
    {
        var found = await consults.GetByIdAsync(id, context.RequestAborted).ConfigureAwait(false);
        return found is null ? NotFound() : Results.Json(found, Json);
    }

    internal static async Task<IResult> GetAdvisorConsultByClassificationAsync(
        Guid id,
        ILocalModelAdvisorConsultStore consults,
        HttpContext context)
    {
        var found = await consults.GetByClassificationIdAsync(id, context.RequestAborted).ConfigureAwait(false);
        return found is null ? NotFound() : Results.Json(found, Json);
    }

    private sealed record ListQuery(string? Text, string? Sort, string? Direction, Guid? Cursor, int Take);

    private static ListQuery ReadListQuery(HttpContext context)
    {
        var query = context.Request.Query;
        var take = int.TryParse(query["take"], out var parsedTake)
            ? Math.Clamp(parsedTake, 1, MaxTake)
            : DefaultTake;
        Guid? cursor = Guid.TryParse(query["cursor"], out var parsedCursor) ? parsedCursor : null;
        return new ListQuery(
            ListFilterText.Normalize(query["q"]),
            query["sort"],
            query["dir"],
            cursor,
            take);
    }

    private static async Task<IResult> ListAsync<T>(
        ListQuery query,
        Func<Guid?, int, ValueTask<KeysetPage<T>>> fetch)
    {
        try
        {
            var page = await fetch(query.Cursor, query.Take).ConfigureAwait(false);
            return Results.Json(new
            {
                items = page.Items,
                nextCursor = page.NextCursor,
                totalCount = page.TotalCount,
                preceding = page.Preceding,
            }, Json);
        }
        catch (Exception ex) when (query.Text is not null && AdminQueryTimeout.IsTimeout(ex))
        {
            return Results.Json(
                new { error = "The search took too long and was stopped.  Try a more specific term." },
                Json,
                statusCode: StatusCodes.Status408RequestTimeout);
        }
    }

    private static int? ParseSeverity(string? value) =>
        int.TryParse(value, out var severity) && severity is >= 1 and <= 10 ? severity : null;

    private static IResult NotFound() =>
        Results.Json(new { error = "Not found." }, Json, statusCode: StatusCodes.Status404NotFound);
}
