using System.Text.Json;
using Microsoft.Extensions.Options;
using Viegard.Application.Actions;
using Viegard.Application.Audit;
using Viegard.Application.Queues;
using Viegard.Application.Stores;
using Viegard.Domain;
using Viegard.Domain.Actions;
using Viegard.Domain.Audit;
using Viegard.PipelineHost.Configuration;

namespace Viegard.PipelineHost.Workers;

public sealed class ActionWorker(
    IWorkQueue<ActionWorkItem> actionQueue,
    IActionStore actionStore,
    IEnumerable<IActionProvider> providers,
    IAuditLedger auditLedger,
    IOptions<ActionWorkerOptions> options,
    TimeProvider timeProvider,
    ILogger<ActionWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IReadOnlyDictionary<string, IActionProvider> _providers = BuildProviderRegistry(providers);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Action worker started for queue '{QueueName}' with providers {Providers}.",
            actionQueue.QueueName,
            string.Join(", ", _providers.Keys.OrderBy(key => key, StringComparer.Ordinal)));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessNextAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Action worker failed while processing an action lease.");
            }
        }

        logger.LogInformation("Action worker stopping.");
    }

    internal async Task ProcessNextAsync(CancellationToken cancellationToken = default)
    {
        IWorkLease<ActionWorkItem>? lease = null;
        try
        {
            lease = await actionQueue.LeaseAsync(cancellationToken).ConfigureAwait(false);
            await ProcessLeaseAsync(lease, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (lease is not null)
            {
                await AbandonLeaseAsync(lease, chargeAttempt: !cancellationToken.IsCancellationRequested)
                    .ConfigureAwait(false);
            }

            throw;
        }
    }

    private async Task ProcessLeaseAsync(IWorkLease<ActionWorkItem> lease, CancellationToken cancellationToken)
    {
        var actionId = lease.Message.ActionId;
        var action = await actionStore.GetAsync(actionId, cancellationToken).ConfigureAwait(false);
        if (action is null)
        {
            logger.LogWarning("Action worker could not find action record {ActionId}; completing lease.", actionId);
            await lease.CompleteAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!_providers.TryGetValue(action.ProviderId, out var provider))
        {
            var failed = action with
            {
                Status = ActionStatus.Failed,
                Error = $"Unknown action provider '{OneLine(action.ProviderId, 80)}'.",
                CompletedAt = timeProvider.GetUtcNow(),
            };
            await StoreAuditAndCompleteAsync(lease, failed, cancellationToken).ConfigureAwait(false);
            return;
        }

        ActionRecord updated;
        try
        {
            updated = provider is IResumableActionProvider resumable
                ? await resumable.ExecuteAsync(action, cancellationToken).ConfigureAwait(false)
                : await provider.ExecuteAsync(ToRequest(action), cancellationToken).ConfigureAwait(false);
            updated = PreserveWorkerOwnedFields(action, updated);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Action provider {ProviderId} threw while executing action {ActionId}.", action.ProviderId, action.Id);
            updated = action with
            {
                Status = ActionStatus.Failed,
                Error = "Action provider threw an exception while executing.",
                CompletedAt = timeProvider.GetUtcNow(),
            };
        }

        await actionStore.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
        await AuditAsync(updated, cancellationToken).ConfigureAwait(false);

        if (updated.Status == ActionStatus.Pending)
        {
            var retryDelay = options.Value.RetryDelay;
            if (retryDelay > TimeSpan.Zero)
            {
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }

            await actionQueue.EnqueueAsync(new ActionWorkItem(updated.Id), cancellationToken).ConfigureAwait(false);
        }

        await lease.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task StoreAuditAndCompleteAsync(
        IWorkLease<ActionWorkItem> lease,
        ActionRecord action,
        CancellationToken cancellationToken)
    {
        await actionStore.UpsertAsync(action, cancellationToken).ConfigureAwait(false);
        await AuditAsync(action, cancellationToken).ConfigureAwait(false);
        await lease.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask AuditAsync(ActionRecord action, CancellationToken cancellationToken)
    {
        await auditLedger.AppendAsync(new AuditRecord
        {
            Id = ViegardId.New(),
            Timestamp = timeProvider.GetUtcNow(),
            Stage = PipelineStage.Action,
            Summary = $"Action {action.ProviderId}/{action.OperationId} ended as {action.Status} for decision {action.DecisionId}.",
            DecisionId = action.DecisionId,
            ActionId = action.Id,
            DetailJson = JsonSerializer.Serialize(new
            {
                action.ProviderId,
                action.OperationId,
                action.Status,
                Error = action.Error is null ? null : OneLine(action.Error, 160),
                HasResults = !string.IsNullOrWhiteSpace(action.ResultsJson),
            }, Json),
        }, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyDictionary<string, IActionProvider> BuildProviderRegistry(IEnumerable<IActionProvider> providers)
    {
        var groups = providers
            .GroupBy(provider => provider.ProviderId, StringComparer.Ordinal)
            .ToList();
        var duplicate = groups.FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Duplicate action provider registration for '{duplicate.Key}'.");
        }

        return groups.ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
    }

    private static ActionRequest ToRequest(ActionRecord action) => new()
    {
        DecisionId = action.DecisionId,
        ProviderId = action.ProviderId,
        OperationId = action.OperationId,
        ParametersJson = action.ParametersJson,
    };

    private static ActionRecord PreserveWorkerOwnedFields(ActionRecord current, ActionRecord updated) =>
        updated with
        {
            Id = current.Id,
            DecisionId = current.DecisionId,
            ProviderId = current.ProviderId,
            OperationId = current.OperationId,
            RequestedAt = current.RequestedAt,
        };

    private async Task AbandonLeaseAsync(IWorkLease<ActionWorkItem> lease, bool chargeAttempt)
    {
        try
        {
            await lease.AbandonAsync(chargeAttempt, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception abandonEx)
        {
            logger.LogError(
                abandonEx,
                chargeAttempt
                    ? "Action worker failed to abandon an action lease."
                    : "Action worker failed to release an action lease during shutdown.");
        }
    }

    private static string OneLine(string? value, int maxChars)
    {
        var sanitized = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
        return sanitized.Length <= maxChars ? sanitized : sanitized[..maxChars];
    }
}
