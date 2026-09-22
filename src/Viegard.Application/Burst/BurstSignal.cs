using Viegard.Domain.Events;

namespace Viegard.Application.Burst;

public static class BurstSignalIds
{
    /// <summary>Repeated admin authentication failures from a single source.</summary>
    public const string AuthFailure = "auth-failure";
}

/// <summary>
/// A standalone burst signal: it decides whether a normalized event counts, and
/// what source key it counts against.  Unlike <c>IDetectionRule</c>, counting and
/// windowing are handled by the detector engine, not the signal.
/// </summary>
public interface IBurstSignal
{
    /// <summary>Stable identifier, matched against <see cref="BurstDetectionValues.ForSignal"/>.</summary>
    string SignalId { get; }

    /// <summary>Whether this event is an occurrence of the signal.</summary>
    bool Matches(NormalizedEvent normalizedEvent);

    /// <summary>The key the occurrence is counted against (for example the source IP), or null to ignore.</summary>
    string? SourceKey(NormalizedEvent normalizedEvent);
}

/// <summary>
/// Counts repeated admin authentication failures (bad password, failed TOTP,
/// failed step-up, failed WebAuthn assertion) from a single source address.
/// Lockout events are excluded because they are a downstream aggregate of the
/// failures already counted here.
/// </summary>
public sealed class AuthFailureBurstSignal : IBurstSignal
{
    public string SignalId => BurstSignalIds.AuthFailure;

    public bool Matches(NormalizedEvent normalizedEvent)
    {
        ArgumentNullException.ThrowIfNull(normalizedEvent);
        return normalizedEvent.Payload is AdminAuthEvent auth && IsFailureKind(auth.Kind);
    }

    public string? SourceKey(NormalizedEvent normalizedEvent)
    {
        ArgumentNullException.ThrowIfNull(normalizedEvent);
        if (normalizedEvent.Payload is not AdminAuthEvent auth)
        {
            return null;
        }

        var address = PrimaryIp(normalizedEvent) ?? auth.RemoteAddress;
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        return "ip=" + address.Trim();
    }

    private static bool IsFailureKind(AdminAuthEventKind kind) => kind switch
    {
        AdminAuthEventKind.LoginFailed => true,
        AdminAuthEventKind.TotpFailed => true,
        AdminAuthEventKind.StepUpFailed => true,
        AdminAuthEventKind.WebAuthnFailed => true,
        _ => false,
    };

    private static string? PrimaryIp(NormalizedEvent normalizedEvent) =>
        (normalizedEvent.Entities ?? Array.Empty<EntityRef>())
            .FirstOrDefault(e => e.Kind == EntityKind.IpAddress && !string.IsNullOrWhiteSpace(e.Value))
            .Value;
}
