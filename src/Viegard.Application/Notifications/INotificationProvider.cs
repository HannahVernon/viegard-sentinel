namespace Viegard.Application.Notifications;

public enum NotificationSeverity
{
    Info,
    Warning,
    Critical,
}

/// <summary>A notification to the operator.  Content must never include secrets.</summary>
public sealed record OperatorNotification
{
    public required string Title { get; init; }

    public required string Body { get; init; }

    public required NotificationSeverity Severity { get; init; }

    /// <summary>Related entity for deep-linking from the admin GUI.</summary>
    public Guid? RelatedEntityId { get; init; }
}

/// <summary>
/// Operator notification delivery (Talons).  Initial implementation: SMTP
/// email.  Mobile push mechanism deferred pending privacy review (D-0015).
/// </summary>
public interface INotificationProvider
{
    string ProviderId { get; }

    Task NotifyAsync(OperatorNotification notification, CancellationToken cancellationToken = default);
}
