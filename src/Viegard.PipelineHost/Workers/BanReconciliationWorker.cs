using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
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
        var posture = policyOptions.Value.Posture;
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

            var read = await ReadAddressListAsync(client, savedRouter, authBytes, cancellationToken).ConfigureAwait(false);
            if (read.Truncated)
            {
                return BanReconciliationRouterResult.Skip(
                    savedRouter.Id,
                    savedRouter.Name,
                    $"Viegard-owned address-list response exceeded {MaxListBodyBytes.ToString(CultureInfo.InvariantCulture)} bytes.");
            }

            var actual = ParseAddressListEntries(read.Body);
            var actualIps = actual
                .Where(entry => entry.CanonicalIp is not null)
                .Select(entry => entry.CanonicalIp!)
                .ToHashSet(StringComparer.Ordinal);
            var missing = desired.Values
                .Where(candidate => !actualIps.Contains(candidate.Ban.Ip))
                .OrderBy(candidate => candidate.Ban.Ip, StringComparer.Ordinal)
                .ToList();
            var extraneous = actual
                .Where(entry => entry.CanonicalIp is null || !desired.ContainsKey(entry.CanonicalIp))
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
                    if (await AddMissingAsync(client, savedRouter, item.Ban, authBytes, cancellationToken).ConfigureAwait(false))
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
                if (string.IsNullOrWhiteSpace(entry.Id))
                {
                    logger.LogWarning(
                        "Ban reconciliation could not remove an extraneous router entry on {RouterName} because the entry id was missing.",
                        savedRouter.Name);
                    continue;
                }

                try
                {
                    if (await RemoveExtraneousAsync(client, savedRouter, entry.Id, authBytes, cancellationToken).ConfigureAwait(false))
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

    private async Task<AddressListReadResult> ReadAddressListAsync(
        HttpClient client,
        MikroTikRouter router,
        byte[] authBytes,
        CancellationToken cancellationToken)
    {
        using var request = AuthorizedRequest(HttpMethod.Get, AddressListQueryUrl(router), authBytes);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var body = await ReadBodyPrefixTextAsync(response.Content, MaxListBodyBytes, cancellationToken).ConfigureAwait(false);
        if (body.Truncated)
        {
            return new AddressListReadResult(body.Text, true);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Router address-list read returned HTTP {(int)response.StatusCode} {OneLine(response.ReasonPhrase, 80)}.".Trim());
        }

        return new AddressListReadResult(body.Text, false);
    }

    private async Task<bool> AddMissingAsync(
        HttpClient client,
        MikroTikRouter router,
        ActiveBan ban,
        byte[] authBytes,
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

        var body = JsonSerializer.Serialize(
            new
            {
                list = MikroTikBanActionProvider.AddressListName,
                address = ban.Ip,
                timeout = MikroTikBanActionProvider.FormatRouterOsDuration(TimeSpan.FromSeconds(remainingSeconds)),
                comment = $"viegard reconciled {ban.DecisionId:N}",
            },
            Json);
        using var request = AuthorizedRequest(HttpMethod.Put, AddressListUrl(router), authBytes);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var responseBody = await ReadBodyPrefixTextAsync(response.Content, MaxErrorBodyBytes, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        if (response.StatusCode == HttpStatusCode.BadRequest
            && responseBody.Text.Contains("already have such entry", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        throw new InvalidOperationException(
            $"Router add returned HTTP {(int)response.StatusCode} {OneLine(response.ReasonPhrase, 80)}.".Trim());
    }

    private static async Task<bool> RemoveExtraneousAsync(
        HttpClient client,
        MikroTikRouter router,
        string entryId,
        byte[] authBytes,
        CancellationToken cancellationToken)
    {
        using var request = AuthorizedRequest(HttpMethod.Delete, AddressListEntryUrl(router, entryId), authBytes);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        _ = await ReadBodyPrefixTextAsync(response.Content, MaxErrorBodyBytes, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
        {
            return true;
        }

        throw new InvalidOperationException(
            $"Router delete returned HTTP {(int)response.StatusCode} {OneLine(response.ReasonPhrase, 80)}.".Trim());
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

    private static IReadOnlyList<RouterAddressListEntry> ParseAddressListEntries(string body)
    {
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Router address-list response was not a JSON array.");
        }

        var entries = new List<RouterAddressListEntry>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                entries.Add(new RouterAddressListEntry(null, string.Empty, null));
                continue;
            }

            var id = GetString(element, ".id");
            var address = GetString(element, "address") ?? string.Empty;
            entries.Add(new RouterAddressListEntry(
                id,
                address,
                TryCanonicalizeIp(address, out var ip) ? ip : null));
        }

        return entries;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool TryCanonicalizeIp(string? value, out string ip)
    {
        ip = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!IPAddress.TryParse(value.Trim(), out var parsed))
        {
            return false;
        }

        ip = parsed.ToString();
        return true;
    }

    private static async ValueTask<BodyPrefixReadResult> ReadBodyPrefixTextAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[maxBytes + 1];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        var truncated = offset > maxBytes;
        return new BodyPrefixReadResult(Encoding.UTF8.GetString(buffer, 0, Math.Min(offset, maxBytes)), truncated);
    }

    private static HttpRequestMessage AuthorizedRequest(HttpMethod method, string url, byte[] authBytes)
    {
        var request = new HttpRequestMessage(method, new Uri(url));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
        return request;
    }

    private static string AddressListUrl(MikroTikRouter router) =>
        $"{router.BaseUrl.TrimEnd('/')}/rest/ip/firewall/address-list";

    private static string AddressListQueryUrl(MikroTikRouter router) =>
        $"{AddressListUrl(router)}?list={Uri.EscapeDataString(MikroTikBanActionProvider.AddressListName)}";

    private static string AddressListEntryUrl(MikroTikRouter router, string id) =>
        $"{AddressListUrl(router)}/{Uri.EscapeDataString(id)}";

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

    private static string RemovalDisplay(RouterAddressListEntry entry) =>
        entry.CanonicalIp ?? OneLine(entry.Address, 80);

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

    private sealed record RouterAddressListEntry(string? Id, string Address, string? CanonicalIp);

    private sealed record AddressListReadResult(string Body, bool Truncated);

    private sealed record BodyPrefixReadResult(string Text, bool Truncated);
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
