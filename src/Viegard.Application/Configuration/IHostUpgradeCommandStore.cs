using Viegard.Domain;

namespace Viegard.Application.Configuration;

/// <summary>
/// Fixed-verb host upgrade command store.  Requests are intentionally limited
/// to a target name, with no operator-supplied command parameters.
/// </summary>
public interface IHostUpgradeCommandStore
{
    ValueTask<HostUpgradeCommand> RequestAsync(
        string target,
        string requestedBy,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<HostUpgradeCommand>> ListRecentAsync(
        string? target = null,
        int limit = HostUpgradeCommandPolicy.DefaultRecentLimit,
        CancellationToken cancellationToken = default);

    ValueTask<HostUpgradeCommand?> ClaimNextPendingAsync(
        string target,
        CancellationToken cancellationToken = default);

    ValueTask<HostUpgradeCommand?> CompleteAsync(
        Guid id,
        bool succeeded,
        string? detail,
        CancellationToken cancellationToken = default);
}

public enum HostUpgradeCommandStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Superseded = 4,
}

public enum HostUpgradeCommandRejectionReason
{
    SingleFlight,
    Cooldown,
}

public sealed record HostUpgradeCommand
{
    public required Guid Id { get; init; }

    public required string Target { get; init; }

    public required HostUpgradeCommandStatus Status { get; init; }

    public required DateTimeOffset RequestedAt { get; init; }

    public required string RequestedBy { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }

    public string? Detail { get; init; }
}

public static class HostUpgradeCommandPolicy
{
    public const string DefaultTarget = "vm";
    public const int MaxTargetLength = 64;
    public const int MaxRequestedByLength = 128;
    public const int CooldownMinutes = 10;
    public const int DefaultRecentLimit = 20;
    public const int MaxRecentLimit = 100;
    public const int PendingStaleAfterMinutes = 3;

    /// <summary>Minimum time between finished upgrades for the same target.</summary>
    public static TimeSpan Cooldown => TimeSpan.FromMinutes(CooldownMinutes);

    /// <summary>Age after which a pending command probably means no host-side agent claimed it.</summary>
    public static TimeSpan PendingStaleAfter => TimeSpan.FromMinutes(PendingStaleAfterMinutes);

    public static HostUpgradeCommand NewRequest(string target, string requestedBy, DateTimeOffset requestedAt) => new()
    {
        Id = ViegardId.New(),
        Target = NormalizeTarget(target),
        Status = HostUpgradeCommandStatus.Pending,
        RequestedAt = requestedAt.ToUniversalTime(),
        RequestedBy = NormalizeRequestedBy(requestedBy),
    };

    public static string NormalizeTarget(string target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var normalized = target.Trim();
        if (normalized.Length is 0 or > MaxTargetLength)
        {
            throw new ArgumentException(
                $"Target must be 1-{MaxTargetLength.ToString(System.Globalization.CultureInfo.InvariantCulture)} characters.",
                nameof(target));
        }

        foreach (var character in normalized)
        {
            var valid = character is >= 'a' and <= 'z'
                || character is >= '0' and <= '9'
                || character is '-' or '_';
            if (!valid)
            {
                throw new ArgumentException(
                    "Target must use only lowercase letters, digits, dash, and underscore.",
                    nameof(target));
            }
        }

        return normalized;
    }

    public static string NormalizeRequestedBy(string requestedBy)
    {
        ArgumentNullException.ThrowIfNull(requestedBy);
        var normalized = requestedBy.Trim();
        if (normalized.Length is 0 or > MaxRequestedByLength)
        {
            throw new ArgumentException(
                $"Requested by must be 1-{MaxRequestedByLength.ToString(System.Globalization.CultureInfo.InvariantCulture)} characters.",
                nameof(requestedBy));
        }

        return normalized;
    }

    public static bool IsPendingStale(HostUpgradeCommand command, DateTimeOffset now) =>
        command.Status == HostUpgradeCommandStatus.Pending
        && command.RequestedAt.Add(PendingStaleAfter) < now.ToUniversalTime();
}

public sealed class HostUpgradeCommandRejectedException : InvalidOperationException
{
    public HostUpgradeCommandRejectedException(
        HostUpgradeCommandRejectionReason reason,
        string target,
        DateTimeOffset? retryAt = null)
        : base(BuildMessage(reason, target, retryAt))
    {
        Reason = reason;
        Target = target;
        RetryAt = retryAt;
    }

    public HostUpgradeCommandRejectionReason Reason { get; }

    public string Target { get; }

    public DateTimeOffset? RetryAt { get; }

    private static string BuildMessage(
        HostUpgradeCommandRejectionReason reason,
        string target,
        DateTimeOffset? retryAt) =>
        reason switch
        {
            HostUpgradeCommandRejectionReason.SingleFlight =>
                $"An upgrade request for {target} is already pending or running.",
            HostUpgradeCommandRejectionReason.Cooldown =>
                retryAt is null
                    ? $"The latest upgrade for {target} finished less than {HostUpgradeCommandPolicy.CooldownMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)} minutes ago."
                    : $"The latest upgrade for {target} finished recently.  Try again after {retryAt.Value.UtcDateTime:u}.",
            _ => "The upgrade request was rejected.",
        };
}
