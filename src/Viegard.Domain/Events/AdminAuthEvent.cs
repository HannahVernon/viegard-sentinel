using System.Text.Json.Serialization;

namespace Viegard.Domain.Events;

public enum AdminAuthEventKind
{
    LoginFailed,
    LoginSucceeded,
    LockoutTriggered,
    TotpFailed,
    RecoveryCodeUsed,
    SessionRevoked,
    StepUpFailed,
    StepUpSucceeded,
    PasswordChanged,
    TotpEnrolled,
    WebAuthnEnrolled,
    WebAuthnFailed,
    WebAuthnRemoved,
    WebAuthnCloneWarning,
}

/// <summary>Security event emitted by the admin authentication boundary.</summary>
public sealed record AdminAuthEvent : EventPayload
{
    [JsonConverter(typeof(JsonStringEnumConverter<AdminAuthEventKind>))]
    public required AdminAuthEventKind Kind { get; init; }

    /// <summary>Operator-supplied username.  Treat as untrusted.</summary>
    public required string Username { get; init; }

    /// <summary>Client address observed by ASP.NET Core.  Treat as untrusted when forwarded headers are enabled.</summary>
    public required string RemoteAddress { get; init; }

    /// <summary>HTTP User-Agent header supplied by the client.  Treat as untrusted.</summary>
    public required string UserAgent { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }
}
