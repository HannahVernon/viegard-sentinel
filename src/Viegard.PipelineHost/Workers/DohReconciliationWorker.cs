using System.Globalization;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Viegard.Actions.MikroTik;
using Viegard.Application.Audit;
using Viegard.Application.Configuration;
using Viegard.Application.Doh;
using Viegard.Application.Policy;
using Viegard.Domain;
using Viegard.Domain.Audit;
using Viegard.PipelineHost.Configuration;

namespace Viegard.PipelineHost.Workers;

/// <summary>
/// Reconciles the confirmed/curated DoH candidate set into the pre-existing
/// <c>dns_over_https_servers</c> address list on every managed MikroTik router.
/// Viegard owns the list in full, so reconciliation is a full sync.  Each entry
/// carries a comment annotating its probe-confirmation state.  Reconciliation is
/// propose-only unless <see cref="DohBlocklistSettings.ApplyToRouters"/> is set
/// and global dry-run is off.
/// </summary>
public sealed class DohReconciliationWorker(
    IDohBlocklistSettingsStore settingsStore,
    IDohDesiredAddressStore desiredStore,
    IDohProbeResultStore probeResultStore,
    IDohReconciliationProposalStore proposalStore,
    IMikroTikRouterStore routerStore,
    IRouterCredentialProtector credentialProtector,
    IOptions<ActionWorkerOptions> options,
    IOptions<DohBlocklistOptions> dohOptions,
    IOptions<PolicyOptions> policyOptions,
    PolicyPostureSource postureSource,
    IMikroTikRouterHttpClientFactory httpClientFactory,
    IAuditLedger auditLedger,
    TimeProvider timeProvider,
    ILogger<DohReconciliationWorker> logger) : BackgroundService
{
    private const int MaxListBodyBytes = 512 * 1024;
    private const int MaxErrorBodyBytes = 32 * 1024;
    private const int MaxCommentLength = 255;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "DoH reconciliation worker started. Interval={Interval}.",
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

        logger.LogInformation("DoH reconciliation worker stopping.");
    }

    internal async Task<DohReconciliationCycleResult> RunCycleAsync(CancellationToken cancellationToken = default)
    {
        var posture = postureSource.CurrentValues(policyOptions.Value);
        var now = timeProvider.GetUtcNow();
        var settings = await settingsStore.GetAsync(cancellationToken).ConfigureAwait(false)
            ?? DohBlocklistSettings.FromOptions(dohOptions.Value, now);
        if (!settings.Enabled)
        {
            logger.LogInformation("DoH reconciliation skipped because the feature is disabled.");
            return DohReconciliationCycleResult.CreateDisabled(now, settings.AddressListName);
        }

        var dryRun = posture.DryRun || !settings.ApplyToRouters;

        var desired = (await desiredStore.ListAsync(cancellationToken).ConfigureAwait(false))
            .OrderBy(address => address.Address, StringComparer.Ordinal)
            .ToList();
        var probeResults = (await probeResultStore.ListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(result => result.Address, StringComparer.Ordinal);
        var desiredComments = desired.ToDictionary(
            address => address.Address,
            address => RenderComment(probeResults.GetValueOrDefault(address.Address), now),
            StringComparer.Ordinal);

        var routers = (await routerStore.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(router => router.Enabled)
            .OrderBy(router => router.Name, StringComparer.Ordinal)
            .ToList();

        var results = new List<DohReconciliationRouterResult>(routers.Count);
        foreach (var router in routers)
        {
            try
            {
                var routerResult = await ConvergeRouterAsync(
                    router,
                    settings.AddressListName,
                    desiredComments,
                    dryRun,
                    cancellationToken).ConfigureAwait(false);
                if (routerResult.Skipped)
                {
                    logger.LogWarning(
                        "DoH reconciliation skipped router {RouterName}: {Detail}",
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
                    "DoH reconciliation skipped router {RouterName}: {Detail}",
                    SafeRouterName(router.Name),
                    detail);
                results.Add(DohReconciliationRouterResult.Skip(router.Id, SafeRouterName(router.Name), detail));
            }
        }

        var cycle = new DohReconciliationCycleResult(now, settings.AddressListName, dryRun, false, results);

        var proposal = new DohReconciliationProposal
        {
            GeneratedAt = now,
            DryRun = dryRun,
            AddressListName = settings.AddressListName,
            DesiredCount = desired.Count,
            Routers = results.Select(result => new DohRouterProposal
            {
                RouterName = result.RouterName,
                Skipped = result.Skipped,
                Detail = result.Detail,
                ToAdd = result.Added,
                ToRemove = result.Removed,
                CommentUpdate = result.CommentUpdated,
            }).ToList(),
        };
        try
        {
            await proposalStore.SaveAsync(proposal, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "DoH reconciliation could not persist the latest proposal snapshot.");
        }

        if (!cycle.HasChanges)
        {
            logger.LogDebug(
                "DoH reconciliation cycle completed with no router changes. DesiredCount={DesiredCount}; RouterCount={RouterCount}; AddressListName={AddressListName}; DryRun={DryRun}.",
                desired.Count,
                routers.Count,
                settings.AddressListName,
                dryRun);
            return cycle;
        }

        var pendingCount = cycle.ProposedAdded + cycle.ProposedRemoved + cycle.ProposedCommentUpdated;
        await auditLedger.AppendAsync(new AuditRecord
        {
            Id = ViegardId.New(),
            Timestamp = now,
            Stage = PipelineStage.Action,
            SourceId = "doh-reconciliation",
            Summary = dryRun
                ? $"DoH reconciliation proposal: {pendingCount} router address-list changes pending (propose-only)."
                : $"DoH reconciliation changed {cycle.TotalAdded + cycle.TotalRemoved + cycle.TotalCommentUpdated} router address-list entries.",
            DetailJson = JsonSerializer.Serialize(new
            {
                DryRun = dryRun,
                settings.AddressListName,
                Routers = cycle.Routers.Select(result => new
                {
                    result.RouterName,
                    result.Skipped,
                    result.Detail,
                    result.Added,
                    result.Removed,
                    result.CommentUpdated,
                }).ToList(),
            }, Json),
        }, cancellationToken).ConfigureAwait(false);

        if (dryRun)
        {
            logger.LogInformation(
                "DoH reconciliation proposal recorded. PendingAdd={Add}; PendingRemove={Remove}; PendingCommentUpdate={CommentUpdate}; AddressListName={AddressListName}.",
                cycle.ProposedAdded,
                cycle.ProposedRemoved,
                cycle.ProposedCommentUpdated,
                settings.AddressListName);
            return cycle;
        }

        logger.LogInformation(
            "DoH reconciliation changed router address-list entries. Added={Added}; Removed={Removed}; CommentUpdated={CommentUpdated}; AddressListName={AddressListName}.",
            cycle.TotalAdded,
            cycle.TotalRemoved,
            cycle.TotalCommentUpdated,
            settings.AddressListName);
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
            logger.LogError(ex, "DoH reconciliation worker failed during a cycle.");
        }
    }

    private async Task<DohReconciliationRouterResult> ConvergeRouterAsync(
        MikroTikRouter router,
        string addressListName,
        IReadOnlyDictionary<string, string> desiredComments,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var savedRouter = MikroTikRouterValidator.NormalizeForSave(router);
        var ciphertext = await routerStore.GetCredentialCiphertextAsync(savedRouter.Id, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(ciphertext))
        {
            return DohReconciliationRouterResult.Skip(savedRouter.Id, savedRouter.Name, "Router credential is not set.");
        }

        using var client = httpClientFactory.CreateClient(savedRouter, out var transportState);
        byte[] authBytes = [];
        try
        {
            var password = credentialProtector.Unprotect(savedRouter.Id, ciphertext);
            authBytes = Encoding.UTF8.GetBytes($"{savedRouter.Username}:{password}");
            string[] sensitiveValues = [savedRouter.Username, password];

            var addressListClient = new MikroTikAddressListClient(client, savedRouter, authBytes);
            var read = await addressListClient.ReadListAsync(addressListName, MaxListBodyBytes, cancellationToken).ConfigureAwait(false);
            if (read.Truncated)
            {
                return DohReconciliationRouterResult.Skip(
                    savedRouter.Id,
                    savedRouter.Name,
                    $"DoH address-list response exceeded {MaxListBodyBytes.ToString(CultureInfo.InvariantCulture)} bytes.");
            }

            var parsed = MikroTikAddressListClient.ParseEntries(read.Body);
            var actual = parsed.Where(entry => entry.IsInList(addressListName)).ToList();
            var actualByAddress = new Dictionary<string, MikroTikAddressListEntry>(StringComparer.Ordinal);
            foreach (var entry in actual.Where(entry => entry.CanonicalAddress is not null))
            {
                actualByAddress[entry.CanonicalAddress!] = entry;
            }

            var missing = desiredComments.Keys
                .Where(address => !actualByAddress.ContainsKey(address))
                .OrderBy(address => address, StringComparer.Ordinal)
                .ToList();
            var commentDrift = desiredComments
                .Where(pair => actualByAddress.TryGetValue(pair.Key, out var entry)
                    && !string.Equals(entry.Comment ?? string.Empty, pair.Value, StringComparison.Ordinal))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToList();
            var extraneous = actual
                .Where(entry => entry.CanonicalAddress is null || !desiredComments.ContainsKey(entry.CanonicalAddress))
                .ToList();

            if (dryRun)
            {
                if (missing.Count > 0 || extraneous.Count > 0 || commentDrift.Count > 0)
                {
                    logger.LogInformation(
                        "DoH reconciliation dry-run would change router {RouterName}. Add={AddCount}; Remove={RemoveCount}; CommentUpdate={CommentCount}.",
                        savedRouter.Name,
                        missing.Count,
                        extraneous.Count,
                        commentDrift.Count);
                }

                return new DohReconciliationRouterResult(
                    savedRouter.Id,
                    savedRouter.Name,
                    false,
                    null,
                    missing,
                    extraneous.Select(entry => string.IsNullOrWhiteSpace(entry.CanonicalAddress) ? entry.RawAddress : entry.CanonicalAddress!).ToList(),
                    commentDrift.Select(pair => pair.Key).ToList());
            }

            var added = new List<string>();
            var removed = new List<string>();
            var commentUpdated = new List<string>();

            foreach (var address in missing)
            {
                try
                {
                    _ = await addressListClient
                        .PutAsync(addressListName, address, MaxErrorBodyBytes, comment: desiredComments[address], cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    added.Add(address);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    return new DohReconciliationRouterResult(savedRouter.Id, savedRouter.Name, true, RouterFailureDetail(ex), added, removed, commentUpdated);
                }
            }

            foreach (var pair in commentDrift)
            {
                var entry = actualByAddress[pair.Key];
                if (!MikroTikAddressListClient.IsValidEntryId(entry.Id))
                {
                    continue;
                }

                try
                {
                    _ = await addressListClient
                        .SetCommentAsync(entry.Id, pair.Value, MaxErrorBodyBytes, cancellationToken)
                        .ConfigureAwait(false);
                    commentUpdated.Add(pair.Key);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    return new DohReconciliationRouterResult(savedRouter.Id, savedRouter.Name, true, RouterFailureDetail(ex), added, removed, commentUpdated);
                }
            }

            foreach (var entry in extraneous)
            {
                if (!MikroTikAddressListClient.IsValidEntryId(entry.Id))
                {
                    logger.LogWarning(
                        "DoH reconciliation could not remove an extraneous router entry on {RouterName} because the entry id was missing or unrecognized.",
                        savedRouter.Name);
                    continue;
                }

                try
                {
                    _ = await addressListClient
                        .DeleteAsync(entry.Id, MaxErrorBodyBytes, sensitiveValues, cancellationToken)
                        .ConfigureAwait(false);
                    removed.Add(string.IsNullOrWhiteSpace(entry.CanonicalAddress) ? entry.RawAddress : entry.CanonicalAddress!);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    return new DohReconciliationRouterResult(savedRouter.Id, savedRouter.Name, true, RouterFailureDetail(ex), added, removed, commentUpdated);
                }
            }

            return new DohReconciliationRouterResult(savedRouter.Id, savedRouter.Name, false, null, added, removed, commentUpdated);
        }
        catch (HttpRequestException ex) when (transportState.CertificatePinMismatch || ContainsAuthenticationException(ex))
        {
            return DohReconciliationRouterResult.Skip(
                savedRouter.Id,
                savedRouter.Name,
                transportState.CertificatePinMismatch ? "Router TLS certificate pin mismatch." : "Router TLS connection failed.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DohReconciliationRouterResult.Skip(savedRouter.Id, savedRouter.Name, "Router call timed out after 10 seconds.");
        }
        catch (HttpRequestException)
        {
            return DohReconciliationRouterResult.Skip(savedRouter.Id, savedRouter.Name, "Router connection failed.");
        }
        catch (RouterCredentialProtectionException)
        {
            return DohReconciliationRouterResult.Skip(savedRouter.Id, savedRouter.Name, "Router credential could not be decrypted.");
        }
        finally
        {
            Array.Clear(authBytes);
        }
    }

    private static string RenderComment(DohProbeResult? result, DateTimeOffset now)
    {
        var date = now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var comment = result?.Status switch
        {
            DohProbeStatus.Confirmed => $"doh-confirmed {date} 200/dns-message/token-ok",
            null or DohProbeStatus.Unprobed => $"doh-feed {date}",
            _ => $"doh-feed {date} unconfirmed:{result!.Status.ToString().ToLowerInvariant()}",
        };
        return comment.Length <= MaxCommentLength ? comment : comment[..MaxCommentLength];
    }

    private static bool ContainsAuthenticationException(HttpRequestException ex) =>
        ex.InnerException is AuthenticationException;

    private static string RouterFailureDetail(Exception ex) => ex switch
    {
        JsonException => "Router address-list response was not valid JSON.",
        InvalidOperationException invalidOperation => invalidOperation.Message,
        HttpRequestException httpRequest when ContainsAuthenticationException(httpRequest) => "Router TLS connection failed.",
        HttpRequestException => "Router connection failed.",
        OperationCanceledException => "Router call timed out after 10 seconds.",
        RouterCredentialProtectionException => "Router credential could not be decrypted.",
        _ => OneLine(ex.Message, 160),
    };

    private static string SafeRouterName(string value) => OneLine(value, 64);

    private static string OneLine(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized[..maxLength];
    }
}

public sealed record DohReconciliationCycleResult(
    DateTimeOffset StartedAt,
    string AddressListName,
    bool DryRun,
    bool Disabled,
    IReadOnlyList<DohReconciliationRouterResult> Routers)
{
    public bool Changed => TotalAdded > 0 || TotalRemoved > 0 || TotalCommentUpdated > 0;

    /// <summary>True when the cycle proposed or applied any router change (dry-run included).</summary>
    public bool HasChanges => ProposedAdded > 0 || ProposedRemoved > 0 || ProposedCommentUpdated > 0;

    /// <summary>Additions proposed (dry-run) or applied, counted regardless of mode.</summary>
    public int ProposedAdded => Routers.Sum(router => router.Added.Count);

    public int ProposedRemoved => Routers.Sum(router => router.Removed.Count);

    public int ProposedCommentUpdated => Routers.Sum(router => router.CommentUpdated.Count);

    public int TotalAdded => DryRun ? 0 : Routers.Sum(router => router.Added.Count);

    public int TotalRemoved => DryRun ? 0 : Routers.Sum(router => router.Removed.Count);

    public int TotalCommentUpdated => DryRun ? 0 : Routers.Sum(router => router.CommentUpdated.Count);

    public static DohReconciliationCycleResult CreateDisabled(DateTimeOffset startedAt, string addressListName) =>
        new(startedAt, addressListName, false, true, []);
}

public sealed record DohReconciliationRouterResult(
    Guid RouterId,
    string RouterName,
    bool Skipped,
    string? Detail,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> CommentUpdated)
{
    public static DohReconciliationRouterResult Skip(Guid routerId, string routerName, string detail) =>
        new(routerId, routerName, true, detail, [], [], []);
}
