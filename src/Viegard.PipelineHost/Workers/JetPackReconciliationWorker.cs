using System.Globalization;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Viegard.Actions.MikroTik;
using Viegard.Application.Audit;
using Viegard.Application.Configuration;
using Viegard.Domain;
using Viegard.Domain.Audit;
using Viegard.PipelineHost.Configuration;

namespace Viegard.PipelineHost.Workers;

public sealed class JetPackReconciliationWorker(
    IJetPackFeedSettingsStore settingsStore,
    IJetPackDesiredAddressStore desiredStore,
    IMikroTikRouterStore routerStore,
    IRouterCredentialProtector credentialProtector,
    IOptions<ActionWorkerOptions> options,
    IOptions<JetPackFeedOptions> feedOptions,
    IMikroTikRouterHttpClientFactory httpClientFactory,
    IAuditLedger auditLedger,
    TimeProvider timeProvider,
    ILogger<JetPackReconciliationWorker> logger) : BackgroundService
{
    private const int MaxListBodyBytes = 256 * 1024;
    private const int MaxErrorBodyBytes = 32 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "JetPack reconciliation worker started. Interval={Interval}.",
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

        logger.LogInformation("JetPack reconciliation worker stopping.");
    }

    internal async Task<JetPackReconciliationCycleResult> RunCycleAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var settings = await settingsStore.GetAsync(cancellationToken).ConfigureAwait(false)
            ?? JetPackFeedSettings.FromOptions(feedOptions.Value, now);
        if (!settings.Enabled)
        {
            logger.LogInformation("JetPack reconciliation skipped because the feed is disabled.");
            return JetPackReconciliationCycleResult.CreateDisabled(now, settings.AddressListName);
        }

        var desired = (await desiredStore.ListAsync(cancellationToken).ConfigureAwait(false))
            .OrderBy(address => address.Address, StringComparer.Ordinal)
            .ToList();
        var desiredMap = desired.ToDictionary(address => address.Address, StringComparer.Ordinal);
        var routers = (await routerStore.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(router => router.Enabled)
            .OrderBy(router => router.Name, StringComparer.Ordinal)
            .ToList();

        var results = new List<JetPackReconciliationRouterResult>(routers.Count);
        foreach (var router in routers)
        {
            try
            {
                var routerResult = await ConvergeRouterAsync(
                    router,
                    settings.AddressListName,
                    desiredMap,
                    cancellationToken).ConfigureAwait(false);
                if (routerResult.Skipped)
                {
                    logger.LogWarning(
                        "JetPack reconciliation skipped router {RouterName}: {Detail}",
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
                    "JetPack reconciliation skipped router {RouterName}: {Detail}",
                    SafeRouterName(router.Name),
                    detail);
                results.Add(JetPackReconciliationRouterResult.Skip(router.Id, SafeRouterName(router.Name), detail));
            }
        }

        var cycle = new JetPackReconciliationCycleResult(now, settings.AddressListName, false, results);
        if (!cycle.Changed)
        {
            logger.LogDebug(
                "JetPack reconciliation cycle completed with no router changes. DesiredCount={DesiredCount}; RouterCount={RouterCount}; AddressListName={AddressListName}.",
                desiredMap.Count,
                routers.Count,
                settings.AddressListName);
            return cycle;
        }

        await auditLedger.AppendAsync(new AuditRecord
        {
            Id = ViegardId.New(),
            Timestamp = now,
            Stage = PipelineStage.Action,
            Summary = $"JetPack reconciliation changed {cycle.TotalAdded + cycle.TotalRemoved} router address-list entries.",
            DetailJson = JsonSerializer.Serialize(new
            {
                settings.AddressListName,
                Routers = cycle.Routers.Select(result => new
                {
                    result.RouterName,
                    result.Skipped,
                    result.Detail,
                    result.Added,
                    result.Removed,
                }).ToList(),
            }, Json),
        }, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "JetPack reconciliation changed router address-list entries. Added={Added}; Removed={Removed}; RouterCount={RouterCount}; AddressListName={AddressListName}.",
            cycle.TotalAdded,
            cycle.TotalRemoved,
            results.Count(result => result.Added.Count > 0 || result.Removed.Count > 0),
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
            logger.LogError(ex, "JetPack reconciliation worker failed during a cycle.");
        }
    }

    private async Task<JetPackReconciliationRouterResult> ConvergeRouterAsync(
        MikroTikRouter router,
        string addressListName,
        IReadOnlyDictionary<string, JetPackDesiredAddress> desired,
        CancellationToken cancellationToken)
    {
        var savedRouter = MikroTikRouterValidator.NormalizeForSave(router);
        var ciphertext = await routerStore.GetCredentialCiphertextAsync(savedRouter.Id, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(ciphertext))
        {
            return JetPackReconciliationRouterResult.Skip(savedRouter.Id, savedRouter.Name, "Router credential is not set.");
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
                return JetPackReconciliationRouterResult.Skip(
                    savedRouter.Id,
                    savedRouter.Name,
                    $"JetPack address-list response exceeded {MaxListBodyBytes.ToString(CultureInfo.InvariantCulture)} bytes.");
            }

            var parsed = MikroTikAddressListClient.ParseEntries(read.Body);
            var actual = parsed.Where(entry => entry.IsInList(addressListName)).ToList();
            var foreignCount = parsed.Count - actual.Count;
            if (foreignCount > 0)
            {
                logger.LogWarning(
                    "JetPack reconciliation on {RouterName} received {ForeignCount} entries outside address list '{ListName}' despite the filtered query; they were excluded from reconciliation.",
                    savedRouter.Name,
                    foreignCount,
                    addressListName);
            }

            var actualAddresses = actual
                .Where(entry => entry.CanonicalAddress is not null)
                .Select(entry => entry.CanonicalAddress!)
                .ToHashSet(StringComparer.Ordinal);
            var missing = desired.Values
                .Where(candidate => !actualAddresses.Contains(candidate.Address))
                .OrderBy(candidate => candidate.Address, StringComparer.Ordinal)
                .ToList();
            var extraneous = actual
                .Where(entry => entry.CanonicalAddress is null || !desired.ContainsKey(entry.CanonicalAddress))
                .ToList();

            var added = new List<string>();
            var removed = new List<string>();

            foreach (var item in missing)
            {
                try
                {
                    _ = await addressListClient
                        .PutAsync(addressListName, item.Address, MaxErrorBodyBytes, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    added.Add(item.Address);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    return new JetPackReconciliationRouterResult(savedRouter.Id, savedRouter.Name, true, RouterFailureDetail(ex), added, removed);
                }
            }

            foreach (var entry in extraneous)
            {
                if (!MikroTikAddressListClient.IsValidEntryId(entry.Id))
                {
                    logger.LogWarning(
                        "JetPack reconciliation could not remove an extraneous router entry on {RouterName} because the entry id was missing or unrecognized.",
                        savedRouter.Name);
                    continue;
                }

                try
                {
                    _ = await addressListClient
                        .DeleteAsync(entry.Id, MaxErrorBodyBytes, sensitiveValues, cancellationToken)
                        .ConfigureAwait(false);
                    removed.Add(string.IsNullOrWhiteSpace(entry.CanonicalAddress) ? entry.RawAddress : entry.CanonicalAddress);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    return new JetPackReconciliationRouterResult(savedRouter.Id, savedRouter.Name, true, RouterFailureDetail(ex), added, removed);
                }
            }

            return new JetPackReconciliationRouterResult(savedRouter.Id, savedRouter.Name, false, null, added, removed);
        }
        catch (HttpRequestException ex) when (transportState.CertificatePinMismatch || ContainsAuthenticationException(ex))
        {
            return JetPackReconciliationRouterResult.Skip(
                savedRouter.Id,
                savedRouter.Name,
                transportState.CertificatePinMismatch ? "Router TLS certificate pin mismatch." : "Router TLS connection failed.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return JetPackReconciliationRouterResult.Skip(savedRouter.Id, savedRouter.Name, "Router call timed out after 10 seconds.");
        }
        catch (HttpRequestException)
        {
            return JetPackReconciliationRouterResult.Skip(savedRouter.Id, savedRouter.Name, "Router connection failed.");
        }
        catch (RouterCredentialProtectionException)
        {
            return JetPackReconciliationRouterResult.Skip(savedRouter.Id, savedRouter.Name, "Router credential could not be decrypted.");
        }
        finally
        {
            Array.Clear(authBytes);
        }
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

public sealed record JetPackReconciliationCycleResult(
    DateTimeOffset StartedAt,
    string AddressListName,
    bool Disabled,
    IReadOnlyList<JetPackReconciliationRouterResult> Routers)
{
    public bool Changed => TotalAdded > 0 || TotalRemoved > 0;

    public int TotalAdded => Routers.Sum(router => router.Added.Count);

    public int TotalRemoved => Routers.Sum(router => router.Removed.Count);

    public static JetPackReconciliationCycleResult CreateDisabled(DateTimeOffset startedAt, string addressListName) =>
        new(startedAt, addressListName, true, []);
}

public sealed record JetPackReconciliationRouterResult(
    Guid RouterId,
    string RouterName,
    bool Skipped,
    string? Detail,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed)
{
    public static JetPackReconciliationRouterResult Skip(Guid routerId, string routerName, string detail) =>
        new(routerId, routerName, true, detail, [], []);
}
