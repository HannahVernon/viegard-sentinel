using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Viegard.Actions.MikroTik;
using Viegard.Application.Actions;
using Viegard.Application.Audit;
using Viegard.Application.Configuration;
using Viegard.Application.Policy;
using Viegard.Domain;
using Viegard.Domain.Actions;
using Viegard.Domain.Audit;
using Viegard.PipelineHost.Configuration;

namespace Viegard.PipelineHost.Workers;

public sealed class BanReconciliationWorker(
    IActiveBanStore activeBanStore,
    IMikroTikRouterStore routerStore,
    IRouterCredentialProtector credentialProtector,
    IOptions<PolicyOptions> policyOptions,
    PolicyPostureSource postureSource,
    IOptions<ActionWorkerOptions> options,
    IMikroTikRouterHttpClientFactory httpClientFactory,
    IAuditLedger auditLedger,
    TimeProvider timeProvider,
    ILogger<BanReconciliationWorker> logger) : BackgroundService
{
    private const int MaxListBodyBytes = 256 * 1024;
    private const int MaxErrorBodyBytes = 32 * 1024;
    private const int MaxDetailChars = 512;
    private static readonly TimeSpan SkipBanCallRemainingThreshold = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Ban reconciliation worker started. Interval={Interval}.",
            options.Value.ReconciliationInterval);

        try
        {
            await RunCycleSafelyAsync(stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(options.Value.ReconciliationInterval, timeProvider);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RunCycleSafelyAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        logger.LogInformation("Ban reconciliation worker stopping.");
    }

    internal async Task<BanReconciliationCycleResult> RunCycleAsync(CancellationToken cancellationToken = default)
    {
        var posture = postureSource.CurrentValues(policyOptions.Value);
        var now = timeProvider.GetUtcNow();
        if (posture.EmergencyStop)
        {
            logger.LogWarning("Ban reconciliation cycle skipped because Policy posture EmergencyStop is enabled.");
            return BanReconciliationCycleResult.Skipped(now, dryRun: posture.DryRun, "emergency-stop");
        }

        long expiredRowsDeleted = 0;
        if (posture.DryRun)
        {
            logger.LogDebug("Ban reconciliation dry-run skipped expired active-ban cleanup.");
        }
        else
        {
            expiredRowsDeleted = await activeBanStore.DeleteExpiredAsync(now, cancellationToken).ConfigureAwait(false);
        }

        var activeBans = await activeBanStore.ListUnexpiredAsync(now, cancellationToken).ConfigureAwait(false);
        var desired = BuildDesiredMap(activeBans, now);
        var routers = (await routerStore.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(router => router.Enabled)
            .OrderBy(router => router.Name, StringComparer.Ordinal)
            .ToList();

        var results = new List<BanReconciliationRouterResult>(routers.Count);
        foreach (var router in routers)
        {
            try
            {
                var routerResult = await ConvergeRouterAsync(router, desired, posture.DryRun, cancellationToken).ConfigureAwait(false);
                if (routerResult.Skipped)
                {
                    logger.LogWarning(
                        "Ban reconciliation skipped router {RouterName}: {Detail}",
                        routerResult.RouterName,
                        routerResult.Detail);
                }

                results.Add(routerResult);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var detail = RouterFailureDetail(ex);
                logger.LogWarning(
                    "Ban reconciliation skipped router {RouterName}: {Detail}",
                    SafeRouterName(router.Name),
                    detail);
                results.Add(BanReconciliationRouterResult.Skip(router.Id, SafeRouterName(router.Name), detail));
            }
        }

        var cycle = new BanReconciliationCycleResult(now, posture.DryRun, false, expiredRowsDeleted, results);
        if (!cycle.Changed)
        {
            logger.LogDebug(
                "Ban reconciliation cycle completed with no router changes. DesiredCount={DesiredCount}; RouterCount={RouterCount}; ExpiredRowsDeleted={ExpiredRowsDeleted}; DryRun={DryRun}.",
                desired.Count,
                routers.Count,
                expiredRowsDeleted,
                posture.DryRun);
            return cycle;
        }

        await auditLedger.AppendAsync(new AuditRecord
        {
            Id = ViegardId.New(),
            Timestamp = now,
            Stage = PipelineStage.Action,
            Summary = $"Ban reconciliation changed {cycle.TotalAdded + cycle.TotalRemoved} router address-list entries.",
            DetailJson = DetailJson(cycle),
        }, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Ban reconciliation changed router address-list entries. Added={Added}; Removed={Removed}; RouterCount={RouterCount}; ExpiredRowsDeleted={ExpiredRowsDeleted}.",
            cycle.TotalAdded,
            cycle.TotalRemoved,
            results.Count(result => result.Added.Count > 0 || result.Removed.Count > 0),
            expiredRowsDeleted);
        return cycle;
    }

    private async Task RunCycleSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunCycleAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ban reconciliation worker failed during a cycle.");
        }
    }

    private async Task<BanReconciliationRouterResult> ConvergeRouterAsync(
        MikroTikRouter router,
        IReadOnlyDictionary<string, DesiredBan> desired,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var savedRouter = MikroTikRouterValidator.NormalizeForSave(router);
        var ciphertext = await routerStore.GetCredentialCiphertextAsync(savedRouter.Id, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(ciphertext))
        {
            return BanReconciliationRouterResult.Skip(savedRouter.Id, savedRouter.Name, "Router credential is not set.");
        }

        using var client = httpClientFactory.CreateClient(savedRouter, out var transportState);
        byte[] authBytes = [];
        try
        {
            var password = credentialProtector.Unprotect(savedRouter.Id, ciphertext);
            authBytes = Encoding.UTF8.GetBytes($"{savedRouter.Username}:{password}");
            string[] sensitiveValues = [savedRouter.Username, password];

            var addressListClient = new MikroTikAddressListClient(client, savedRouter, authBytes);
            var read = await addressListClient
                .ReadListAsync(MikroTikBanActionProvider.AddressListName, MaxListBodyBytes, cancellationToken)
                .ConfigureAwait(false);
            if (read.Truncated)
            {
                return BanReconciliationRouterResult.Skip(
                    savedRouter.Id,
                    savedRouter.Name,
                    $"Viegard-owned address-list response exceeded {MaxListBodyBytes} bytes.");
            }

            var parsed = MikroTikAddressListClient.ParseEntries(read.Body);
            // Trust nothing about server-side filtering: only entries whose
            // own list property names the Viegard-owned list participate in
            // reconciliation.  Foreign entries are reported and left alone,
            // so a broken query filter can never widen removal scope to
            // address lists Viegard does not own.
            var actual = parsed.Where(entry => entry.IsInList(MikroTikBanActionProvider.AddressListName)).ToList();
            var foreignCount = parsed.Count - actual.Count;
            if (foreignCount > 0)
            {
                logger.LogWarning(
                    "Ban reconciliation on {RouterName} received {ForeignCount} entries outside address list '{ListName}' despite the filtered query; they were excluded from reconciliation.",
                    savedRouter.Name,
                    foreignCount,
                    MikroTikBanActionProvider.AddressListName);
            }

            var actualIps = actual
                .Where(entry => entry.CanonicalAddress is not null)
                .Select(entry => entry.CanonicalAddress!)
                .ToHashSet(StringComparer.Ordinal);
            var missing = desired.Values
                .Where(candidate => !actualIps.Contains(candidate.Ban.Ip))
                .OrderBy(candidate => candidate.Ban.Ip, StringComparer.Ordinal)
                .ToList();
            var extraneous = actual
                .Where(entry => entry.CanonicalAddress is null || !desired.ContainsKey(entry.CanonicalAddress))
                .ToList();

            if (dryRun)
            {
                if (missing.Count > 0 || extraneous.Count > 0)
                {
                    logger.LogInformation(
                        "Ban reconciliation dry-run would change router {RouterName}. Add={AddCount}; Remove={RemoveCount}.",
                        savedRouter.Name,
                        missing.Count,
                        extraneous.Count);
                }

                return new BanReconciliationRouterResult(
                    savedRouter.Id,
                    savedRouter.Name,
                    false,
                    null,
                    missing.Select(item => item.Ban.Ip).ToList(),
                    extraneous.Select(RemovalDisplay).ToList());
            }

            var added = new List<string>();
            var removed = new List<string>();

            // The reconciler and ActionWorker may touch routers concurrently.  The
            // RouterOS operations are idempotent: duplicate adds and absent deletes
            // are tolerated, and any race converges on the next cycle.
            foreach (var item in missing)
            {
                try
                {
                    if (await AddMissingAsync(addressListClient, savedRouter, item.Ban, cancellationToken).ConfigureAwait(false))
                    {
                        added.Add(item.Ban.Ip);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    return new BanReconciliationRouterResult(
                        savedRouter.Id,
                        savedRouter.Name,
                        true,
                        RouterFailureDetail(ex),
                        added,
                        removed);
                }
            }

            foreach (var entry in extraneous)
            {
                if (!MikroTikAddressListClient.IsValidEntryId(entry.Id))
                {
                    logger.LogWarning(
                        "Ban reconciliation could not remove an extraneous router entry on {RouterName} because the entry id was missing or unrecognized.",
                        savedRouter.Name);
                    continue;
                }

                try
                {
                    if (await RemoveExtraneousAsync(addressListClient, entry.Id, sensitiveValues, cancellationToken).ConfigureAwait(false))
                    {
                        removed.Add(RemovalDisplay(entry));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    return new BanReconciliationRouterResult(
                        savedRouter.Id,
                        savedRouter.Name,
                        true,
                        RouterFailureDetail(ex),
                        added,
                        removed);
                }
            }

            return new BanReconciliationRouterResult(savedRouter.Id, savedRouter.Name, false, null, added, removed);
        }
        catch (HttpRequestException ex) when (transportState.CertificatePinMismatch || ContainsAuthenticationException(ex))
        {
            return BanReconciliationRouterResult.Skip(
                savedRouter.Id,
                savedRouter.Name,
                transportState.CertificatePinMismatch ? "Router TLS certificate pin mismatch." : "Router TLS connection failed.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return BanReconciliationRouterResult.Skip(savedRouter.Id, savedRouter.Name, "Router call timed out after 10 seconds.");
        }
        catch (HttpRequestException)
        {
            return BanReconciliationRouterResult.Skip(savedRouter.Id, savedRouter.Name, "Router connection failed.");
        }
        catch (RouterCredentialProtectionException)
        {
            return BanReconciliationRouterResult.Skip(savedRouter.Id, savedRouter.Name, "Router credential could not be decrypted.");
        }
        finally
        {
            Array.Clear(authBytes);
        }
    }

    private async Task<bool> AddMissingAsync(
        MikroTikAddressListClient addressListClient,
        MikroTikRouter router,
        ActiveBan ban,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var remainingSeconds = (long)Math.Floor((ban.ExpiresAt - now).TotalSeconds);
        if (remainingSeconds <= (long)SkipBanCallRemainingThreshold.TotalSeconds)
        {
            logger.LogDebug(
                "Ban reconciliation did not reapply {Ip} on {RouterName} because the active ban is about to expire.",
                ban.Ip,
                router.Name);
            return false;
        }

        _ = await addressListClient
            .PutAsync(
                MikroTikBanActionProvider.AddressListName,
                ban.Ip,
                MaxErrorBodyBytes,
                MikroTikBanActionProvider.FormatRouterOsDuration(TimeSpan.FromSeconds(remainingSeconds)),
                $"viegard reconciled {ban.DecisionId:N}",
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private static async Task<bool> RemoveExtraneousAsync(
        MikroTikAddressListClient addressListClient,
        string entryId,
        IReadOnlyList<string> sensitiveValues,
        CancellationToken cancellationToken)
    {
        _ = await addressListClient
            .DeleteAsync(entryId, MaxErrorBodyBytes, sensitiveValues, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private static IReadOnlyDictionary<string, DesiredBan> BuildDesiredMap(
        IReadOnlyList<ActiveBan> activeBans,
        DateTimeOffset now) =>
        activeBans
            .Select(ban => new DesiredBan(ban, WholeSecondRemaining(ban.ExpiresAt, now)))
            .Where(item => item.Remaining > SkipBanCallRemainingThreshold)
            .GroupBy(item => item.Ban.Ip, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.Ban.CreatedAt).First(), StringComparer.Ordinal);

    private static TimeSpan WholeSecondRemaining(DateTimeOffset expiresAt, DateTimeOffset now)
    {
        var seconds = (long)Math.Floor((expiresAt - now).TotalSeconds);
        return seconds <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(seconds);
    }

    private static string DetailJson(BanReconciliationCycleResult cycle) =>
        JsonSerializer.Serialize(new
        {
            cycle.StartedAt,
            cycle.ExpiredRowsDeleted,
            Routers = cycle.Routers
                .Where(router => router.Added.Count > 0 || router.Removed.Count > 0)
                .Select(router => new
                {
                    router.RouterId,
                    router.RouterName,
                    router.Added,
                    router.Removed,
                })
                .ToList(),
        }, Json);

    private static string RemovalDisplay(MikroTikAddressListEntry entry) =>
        entry.CanonicalAddress ?? OneLine(entry.RawAddress, 80);

    private static string RouterFailureDetail(Exception exception) => exception switch
    {
        JsonException => "Router address-list response was not valid JSON.",
        OperationCanceledException => "Router call timed out after 10 seconds.",
        InvalidOperationException ex => OneLine(ex.Message, MaxDetailChars),
        RouterCredentialProtectionException => "Router credential could not be decrypted.",
        HttpRequestException => "Router connection failed.",
        _ => "Router reconciliation failed.",
    };

    private static bool ContainsAuthenticationException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AuthenticationException)
            {
                return true;
            }
        }

        return false;
    }

    private static string SafeRouterName(string? value) => OneLine(value, 80);

    private static string OneLine(string? value, int maxChars)
    {
        var sanitized = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
        return sanitized.Length <= maxChars ? sanitized : sanitized[..maxChars];
    }

    private sealed record DesiredBan(ActiveBan Ban, TimeSpan Remaining);
}

public sealed record BanReconciliationCycleResult(
    DateTimeOffset StartedAt,
    bool DryRun,
    bool EmergencyStop,
    long ExpiredRowsDeleted,
    IReadOnlyList<BanReconciliationRouterResult> Routers)
{
    public long TotalAdded => DryRun ? 0 : Routers.Sum(router => router.Added.Count);

    public long TotalRemoved => DryRun ? 0 : Routers.Sum(router => router.Removed.Count);

    public bool Changed => TotalAdded > 0 || TotalRemoved > 0;

    public static BanReconciliationCycleResult Skipped(DateTimeOffset startedAt, bool dryRun, string detail) =>
        new(startedAt, dryRun, true, 0, [BanReconciliationRouterResult.Skip(Guid.Empty, "all", detail)]);
}

public sealed record BanReconciliationRouterResult(
    Guid RouterId,
    string RouterName,
    bool Skipped,
    string? Detail,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed)
{
    public static BanReconciliationRouterResult Skip(Guid routerId, string routerName, string detail) =>
        new(routerId, routerName, true, detail, [], []);
}
