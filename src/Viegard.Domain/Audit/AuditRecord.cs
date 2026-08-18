namespace Viegard.Domain.Audit;

/// <summary>The pipeline stage an audit record originates from.</summary>
public enum PipelineStage
{
    Ingestion,
    Normalization,
    Correlation,
    Classification,
    Policy,
    Action,
    Command,
    System,
}

/// <summary>
/// An append-only audit record (Ledger).  Records link the full chain
/// (event, incident, classification, decision, action) so questions like
/// "why was this IP blocked?" are answerable without raw-log reconstruction.
/// Audit records must never contain secrets.
/// </summary>
public sealed record AuditRecord
{
    public required Guid Id { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public required PipelineStage Stage { get; init; }

    /// <summary>Short human-readable description of what happened.</summary>
    public required string Summary { get; init; }

    public string? SourceId { get; init; }

    public Guid? EventId { get; init; }

    public Guid? IncidentId { get; init; }

    public Guid? ClassificationId { get; init; }

    public Guid? DecisionId { get; init; }

    public Guid? ActionId { get; init; }

    /// <summary>Serialized structured detail (never secrets, never full message bodies).</summary>
    public string? DetailJson { get; init; }
}
