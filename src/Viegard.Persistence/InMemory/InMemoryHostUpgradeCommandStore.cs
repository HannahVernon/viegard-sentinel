using Viegard.Application.Configuration;

namespace Viegard.Persistence.InMemory;

/// <summary>Development-only host upgrade command store.  Commands are process-lifetime only.</summary>
public sealed class InMemoryHostUpgradeCommandStore(TimeProvider? timeProvider = null) : IHostUpgradeCommandStore
{
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly List<HostUpgradeCommand> _commands = [];

    public ValueTask<HostUpgradeCommand> RequestAsync(
        string target,
        string requestedBy,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedTarget = HostUpgradeCommandPolicy.NormalizeTarget(target);
        var normalizedRequestedBy = HostUpgradeCommandPolicy.NormalizeRequestedBy(requestedBy);
        var now = _timeProvider.GetUtcNow();

        lock (_sync)
        {
            ThrowIfBlocked(normalizedTarget, now);

            var command = HostUpgradeCommandPolicy.NewRequest(normalizedTarget, normalizedRequestedBy, now);
            _commands.Add(command);
            return ValueTask.FromResult(command);
        }
    }

    public ValueTask<IReadOnlyList<HostUpgradeCommand>> ListRecentAsync(
        string? target = null,
        int limit = HostUpgradeCommandPolicy.DefaultRecentLimit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedTarget = string.IsNullOrWhiteSpace(target)
            ? null
            : HostUpgradeCommandPolicy.NormalizeTarget(target);
        var safeLimit = Math.Clamp(limit, 1, HostUpgradeCommandPolicy.MaxRecentLimit);

        lock (_sync)
        {
            return ValueTask.FromResult<IReadOnlyList<HostUpgradeCommand>>(
                _commands
                    .Where(command => normalizedTarget is null || command.Target == normalizedTarget)
                    .OrderByDescending(command => command.RequestedAt)
                    .ThenByDescending(command => command.Id)
                    .Take(safeLimit)
                    .ToList());
        }
    }

    public ValueTask<HostUpgradeCommand?> ClaimNextPendingAsync(
        string target,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedTarget = HostUpgradeCommandPolicy.NormalizeTarget(target);
        var now = _timeProvider.GetUtcNow();

        lock (_sync)
        {
            var index = _commands
                .Select((command, position) => new { Command = command, Position = position })
                .Where(item => item.Command.Target == normalizedTarget && item.Command.Status == HostUpgradeCommandStatus.Pending)
                .OrderBy(item => item.Command.RequestedAt)
                .ThenBy(item => item.Command.Id)
                .Select(item => item.Position)
                .FirstOrDefault(-1);
            if (index < 0)
            {
                return ValueTask.FromResult<HostUpgradeCommand?>(null);
            }

            var claimed = _commands[index] with
            {
                Status = HostUpgradeCommandStatus.Running,
                StartedAt = now,
                Detail = null,
            };
            _commands[index] = claimed;
            return ValueTask.FromResult<HostUpgradeCommand?>(claimed);
        }
    }

    public ValueTask<HostUpgradeCommand?> CompleteAsync(
        Guid id,
        bool succeeded,
        string? detail,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = _timeProvider.GetUtcNow();

        lock (_sync)
        {
            var index = _commands.FindIndex(command =>
                command.Id == id && command.Status == HostUpgradeCommandStatus.Running);
            if (index < 0)
            {
                return ValueTask.FromResult<HostUpgradeCommand?>(null);
            }

            var completed = _commands[index] with
            {
                Status = succeeded ? HostUpgradeCommandStatus.Succeeded : HostUpgradeCommandStatus.Failed,
                FinishedAt = now,
                Detail = string.IsNullOrEmpty(detail) ? null : detail,
            };
            _commands[index] = completed;
            return ValueTask.FromResult<HostUpgradeCommand?>(completed);
        }
    }

    private void ThrowIfBlocked(string target, DateTimeOffset now)
    {
        if (_commands.Any(command =>
            command.Target == target
            && command.Status is HostUpgradeCommandStatus.Pending or HostUpgradeCommandStatus.Running))
        {
            throw new HostUpgradeCommandRejectedException(
                HostUpgradeCommandRejectionReason.SingleFlight,
                target);
        }

        var newestFinished = _commands
            .Where(command => command.Target == target && command.FinishedAt is not null)
            .OrderByDescending(command => command.FinishedAt)
            .FirstOrDefault();
        if (newestFinished?.FinishedAt is not { } finishedAt)
        {
            return;
        }

        var retryAt = finishedAt.Add(HostUpgradeCommandPolicy.Cooldown);
        if (retryAt > now)
        {
            throw new HostUpgradeCommandRejectedException(
                HostUpgradeCommandRejectionReason.Cooldown,
                target,
                retryAt);
        }
    }
}
