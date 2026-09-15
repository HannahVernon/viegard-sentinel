using Viegard.Domain.Audit;
using Viegard.Domain.Decisions;
using Viegard.Domain.Incidents;

namespace Viegard.Application.Stores;

public sealed record KeysetPage<T>(IReadOnlyList<T> Items, Guid? NextCursor, long TotalCount, long Preceding);

public sealed record EventListFilter(string? Text);

public sealed record IncidentListFilter(string? Text, IncidentState? State);

public sealed record DecisionListFilter(string? Text, DecisionOutcome? Outcome);

public sealed record AuditListFilter(string? Text, PipelineStage? Stage);

public sealed record SignatureListFilter(string? Text);

public enum SortDirection
{
    Asc,
    Desc,
}

public enum EventSortColumn
{
    Occurred,
    Source,
}

public enum IncidentSortColumn
{
    CorrelationKey,
    Window,
    State,
}

public enum DecisionSortColumn
{
    Created,
    Policy,
    Outcome,
    Classification,
}

public enum AuditSortColumn
{
    Timestamp,
    Stage,
    Source,
}

public enum SignatureSortColumn
{
    Name,
    Target,
    Match,
    Category,
    Severity,
    Enabled,
    Updated,
    Version,
}

public readonly record struct ListSort<TColumn>(TColumn Column, SortDirection Direction)
    where TColumn : struct, Enum;

public static class ListSortParser
{
    public static ListSort<EventSortColumn>? ParseEvent(string? key, string? direction) =>
        ToSort(ParseEventColumn(key), direction);

    public static ListSort<IncidentSortColumn>? ParseIncident(string? key, string? direction) =>
        ToSort(ParseIncidentColumn(key), direction);

    public static ListSort<DecisionSortColumn>? ParseDecision(string? key, string? direction) =>
        ToSort(ParseDecisionColumn(key), direction);

    public static ListSort<AuditSortColumn>? ParseAudit(string? key, string? direction) =>
        ToSort(ParseAuditColumn(key), direction);

    public static ListSort<SignatureSortColumn>? ParseSignature(string? key, string? direction) =>
        ToSort(ParseSignatureColumn(key), direction);

    public static SortDirection? ParseDirection(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "asc" => SortDirection.Asc,
            "desc" => SortDirection.Desc,
            _ => null,
        };

    public static string ToQueryValue(SortDirection direction) => direction switch
    {
        SortDirection.Asc => "asc",
        SortDirection.Desc => "desc",
        _ => "desc",
    };

    public static string ToQueryKey(EventSortColumn column) => column switch
    {
        EventSortColumn.Occurred => "occurred",
        EventSortColumn.Source => "source",
        _ => string.Empty,
    };

    public static string ToQueryKey(IncidentSortColumn column) => column switch
    {
        IncidentSortColumn.CorrelationKey => "correlation",
        IncidentSortColumn.Window => "window",
        IncidentSortColumn.State => "state",
        _ => string.Empty,
    };

    public static string ToQueryKey(DecisionSortColumn column) => column switch
    {
        DecisionSortColumn.Created => "created",
        DecisionSortColumn.Policy => "policy",
        DecisionSortColumn.Outcome => "outcome",
        DecisionSortColumn.Classification => "classification",
        _ => string.Empty,
    };

    public static string ToQueryKey(AuditSortColumn column) => column switch
    {
        AuditSortColumn.Timestamp => "timestamp",
        AuditSortColumn.Stage => "stage",
        AuditSortColumn.Source => "source",
        _ => string.Empty,
    };

    public static string ToQueryKey(SignatureSortColumn column) => column switch
    {
        SignatureSortColumn.Name => "name",
        SignatureSortColumn.Target => "target",
        SignatureSortColumn.Match => "match",
        SignatureSortColumn.Category => "category",
        SignatureSortColumn.Severity => "severity",
        SignatureSortColumn.Enabled => "enabled",
        SignatureSortColumn.Updated => "updated",
        SignatureSortColumn.Version => "version",
        _ => string.Empty,
    };

    private static EventSortColumn? ParseEventColumn(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "occurred" => EventSortColumn.Occurred,
            "source" => EventSortColumn.Source,
            _ => null,
        };

    private static IncidentSortColumn? ParseIncidentColumn(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "correlation" => IncidentSortColumn.CorrelationKey,
            "window" => IncidentSortColumn.Window,
            "state" => IncidentSortColumn.State,
            _ => null,
        };

    private static DecisionSortColumn? ParseDecisionColumn(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "created" => DecisionSortColumn.Created,
            "policy" => DecisionSortColumn.Policy,
            "outcome" => DecisionSortColumn.Outcome,
            "classification" => DecisionSortColumn.Classification,
            _ => null,
        };

    private static AuditSortColumn? ParseAuditColumn(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "timestamp" => AuditSortColumn.Timestamp,
            "stage" => AuditSortColumn.Stage,
            "source" => AuditSortColumn.Source,
            _ => null,
        };

    private static SignatureSortColumn? ParseSignatureColumn(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "name" => SignatureSortColumn.Name,
            "target" => SignatureSortColumn.Target,
            "match" => SignatureSortColumn.Match,
            "category" => SignatureSortColumn.Category,
            "severity" => SignatureSortColumn.Severity,
            "enabled" => SignatureSortColumn.Enabled,
            "updated" => SignatureSortColumn.Updated,
            "version" => SignatureSortColumn.Version,
            _ => null,
        };

    private static ListSort<TColumn>? ToSort<TColumn>(TColumn? column, string? direction)
        where TColumn : struct, Enum
    {
        var parsedDirection = ParseDirection(direction);
        return column is { } parsedColumn && parsedDirection is { } safeDirection
            ? new ListSort<TColumn>(parsedColumn, safeDirection)
            : null;
    }
}

public static class ListFilterText
{
    public const int MaxLength = 200;

    public static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= MaxLength ? trimmed : trimmed[..MaxLength];
    }
}

public static class ListFilterParser
{
    public static TEnum? ParseEnum<TEnum>(string? value)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Enum.TryParse<TEnum>(value.Trim(), ignoreCase: true, out var parsed)
            && Enum.IsDefined(typeof(TEnum), parsed)
            ? parsed
            : null;
    }
}
