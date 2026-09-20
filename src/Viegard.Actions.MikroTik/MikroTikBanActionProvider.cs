using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Viegard.Application.Actions;
using Viegard.Application.Configuration;
using Viegard.Application.Policy;
using Viegard.Application.Stores;
using Viegard.Domain;
using Viegard.Domain.Actions;

namespace Viegard.Actions.MikroTik;

public sealed class MikroTikBanActionProvider(
    IMikroTikRouterStore routerStore,
    IActiveBanStore activeBanStore,
    IRouterCredentialProtector credentialProtector,
    ProtectedAddressList protectedAddresses,
    IOptions<PolicyOptions> policyOptions,
    PolicyPostureSource postureSource,
    IMikroTikRouterHttpClientFactory httpClientFactory,
    TimeProvider timeProvider) : IResumableActionProvider
{
    public const string MikroTikProviderId = "mikrotik";
    public const string BanIpOperationId = "ban-ip";
    public const string RemoveBanOperationId = "remove-ban";
    public const string AddressListName = "viegard-banned";
    public const int MaxAttemptsPerRouter = 5;

    private const int MaxBodyBytes = 32 * 1024;
    private const int MaxDetailChars = 512;
    private static readonly TimeSpan MinBanTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SkipBanCallRemainingThreshold = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxBanTimeout = TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ProviderId => MikroTikProviderId;

    public IReadOnlyList<ActionOperationDescriptor> SupportedOperations { get; } =
    [
        new()
        {
            OperationId = BanIpOperationId,
            Description = "Add an IP address to the Viegard MikroTik address list with a timeout.",
            Destructive = false,
            Reversible = true,
        },
        new()
        {
            OperationId = RemoveBanOperationId,
            Description = "Remove an IP address from the Viegard MikroTik address list.",
            Destructive = false,
            Reversible = false,
        },
    ];

    public Task<ActionRecord> ExecuteAsync(ActionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = timeProvider.GetUtcNow();
        var action = new ActionRecord
        {
            Id = ViegardId.New(),
            DecisionId = request.DecisionId,
            ProviderId = request.ProviderId,
            OperationId = request.OperationId,
            ParametersJson = request.ParametersJson,
            Status = ActionStatus.Pending,
            RequestedAt = now,
        };

        return ExecuteAsync(action, cancellationToken);
    }

    public async Task<ActionRecord> ExecuteAsync(ActionRecord actionRecord, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actionRecord);

        if (actionRecord.Status != ActionStatus.Pending)
        {
            return actionRecord;
        }

        var now = timeProvider.GetUtcNow();
        if (!string.Equals(actionRecord.ProviderId, ProviderId, StringComparison.Ordinal))
        {
            return Failed(actionRecord, $"Provider '{actionRecord.ProviderId}' cannot be executed by provider '{ProviderId}'.", now);
        }

        var posture = postureSource.CurrentValues(policyOptions.Value);
        if (posture.EmergencyStop)
        {
            return Failed(
                actionRecord,
                "Policy posture EmergencyStop is enabled; MikroTik action execution refused.",
                now);
        }

        if (!TryNormalizeAction(actionRecord, out var normalized, out var error))
        {
            return Failed(actionRecord, error, now);
        }

        if (protectedAddresses.IsProtected(normalized.Ip))
        {
            return Failed(
                actionRecord,
                $"D-0026 protected-address guard refused to execute MikroTik action for {normalized.Ip}.",
                now,
                normalized);
        }

        var enabledRouters = (await routerStore.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(router => router.Enabled)
            .OrderBy(router => router.Name, StringComparer.Ordinal)
            .ToList();
        if (enabledRouters.Count == 0)
        {
            if (!posture.DryRun)
            {
                _ = await ApplyDesiredStateChangeAsync(actionRecord, normalized, now, cancellationToken).ConfigureAwait(false);
            }

            return Failed(actionRecord, "MikroTik action provider found zero enabled routers.", now, normalized);
        }

        var existingResults = ParseResults(actionRecord.ResultsJson);
        List<MikroTikRouterActionResult> results;
        if (posture.DryRun)
        {
            results = enabledRouters
                .Select(router => DryRunResult(router, normalized, now))
                .ToList();
            return actionRecord with
            {
                ParametersJson = normalized.ParametersJson,
                RollbackJson = normalized.RollbackJson,
                ResultsJson = SerializeResults(results),
                Status = ActionStatus.DryRun,
                Error = null,
                CompletedAt = now,
            };
        }

        var desiredBan = await ApplyDesiredStateChangeAsync(actionRecord, normalized, now, cancellationToken).ConfigureAwait(false);

        results = [];
        foreach (var router in enabledRouters)
        {
            cancellationToken.ThrowIfCancellationRequested();

            existingResults.TryGetValue(router.Id, out var prior);
            if (prior is not null
                && IsAppliedResult(prior))
            {
                results.Add(prior with { RouterName = router.Name });
                continue;
            }

            if (prior?.Attempts >= MaxAttemptsPerRouter)
            {
                results.Add(prior with { RouterName = router.Name, Status = ResultStatuses.Failed });
                continue;
            }

            var result = await ExecuteRouterAsync(router, normalized, desiredBan, prior, cancellationToken).ConfigureAwait(false);
            results.Add(result);
        }

        var finalStatus = DetermineStatus(results);
        return actionRecord with
        {
            ParametersJson = normalized.ParametersJson,
            RollbackJson = normalized.RollbackJson,
            ResultsJson = SerializeResults(results),
            Status = finalStatus,
            Error = finalStatus switch
            {
                ActionStatus.Succeeded => null,
                ActionStatus.Pending => "One or more MikroTik routers failed; retry pending.",
                _ => "One or more MikroTik routers failed after 5 attempts.",
            },
            CompletedAt = finalStatus == ActionStatus.Pending ? null : now,
        };
    }

    public static string FormatRouterOsDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || duration.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "RouterOS duration must be a positive whole-second value.");
        }

        var totalSeconds = (long)duration.TotalSeconds;
        var days = totalSeconds / 86_400;
        totalSeconds %= 86_400;
        var hours = totalSeconds / 3_600;
        totalSeconds %= 3_600;
        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;

        var builder = new StringBuilder();
        AppendComponent(builder, days, 'd');
        AppendComponent(builder, hours, 'h');
        AppendComponent(builder, minutes, 'm');
        AppendComponent(builder, seconds, 's');
        return builder.ToString();
    }

    internal static bool TryParseRouterOsDuration(string? value, out TimeSpan duration)
    {
        duration = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        var index = 0;
        var lastOrder = 0;
        long totalSeconds = 0;
        while (index < text.Length)
        {
            var numberStart = index;
            while (index < text.Length && char.IsDigit(text[index]))
            {
                index++;
            }

            if (numberStart == index
                || index >= text.Length
                || !long.TryParse(text[numberStart..index], NumberStyles.None, CultureInfo.InvariantCulture, out var amount)
                || amount <= 0)
            {
                return false;
            }

            var unit = text[index++];
            var (order, multiplier) = unit switch
            {
                'd' => (1, 86_400L),
                'h' => (2, 3_600L),
                'm' => (3, 60L),
                's' => (4, 1L),
                _ => (0, 0L),
            };
            if (order == 0 || order <= lastOrder)
            {
                return false;
            }

            try
            {
                checked
                {
                    totalSeconds += amount * multiplier;
                }
            }
            catch (OverflowException)
            {
                return false;
            }

            lastOrder = order;
        }

        try
        {
            duration = TimeSpan.FromSeconds(totalSeconds);
            return duration > TimeSpan.Zero;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private async Task<MikroTikRouterActionResult> ExecuteRouterAsync(
        MikroTikRouter router,
        NormalizedAction normalized,
        ActiveBan? desiredBan,
        MikroTikRouterActionResult? prior,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var priorAttempts = prior?.Attempts ?? 0;

        MikroTikRouter savedRouter;
        try
        {
            savedRouter = MikroTikRouterValidator.NormalizeForSave(router);
        }
        catch (InvalidOperationException ex)
        {
            return FailedResult(router, priorAttempts + 1, $"Router configuration is invalid: {OneLine(ex.Message, MaxDetailChars)}", now);
        }

        if (desiredBan is not null)
        {
            var callNow = timeProvider.GetUtcNow();
            var remaining = desiredBan.ExpiresAt - callNow;
            var remainingSeconds = (long)Math.Floor(remaining.TotalSeconds);
            if (remainingSeconds <= (long)SkipBanCallRemainingThreshold.TotalSeconds)
            {
                return AppliedWithoutCallResult(
                    savedRouter,
                    priorAttempts,
                    "Ban was not sent to the router because the desired active ban is about to expire.",
                    callNow);
            }

            normalized = normalized with
            {
                TimeoutText = FormatRouterOsDuration(TimeSpan.FromSeconds(remainingSeconds)),
            };
        }

        var ciphertext = await routerStore.GetCredentialCiphertextAsync(savedRouter.Id, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(ciphertext))
        {
            return FailedResult(savedRouter, priorAttempts + 1, "Router credential is not set.", now);
        }

        using var client = httpClientFactory.CreateClient(savedRouter, out var transportState);
        byte[] authBytes = [];
        var attempts = priorAttempts + 1;
        try
        {
            var password = credentialProtector.Unprotect(savedRouter.Id, ciphertext);
            authBytes = Encoding.UTF8.GetBytes($"{savedRouter.Username}:{password}");
            string[] sensitiveValues = [savedRouter.Username, password];

            var result = normalized.OperationId == BanIpOperationId
                ? await ExecuteBanAsync(client, savedRouter, normalized, authBytes, sensitiveValues, cancellationToken).ConfigureAwait(false)
                : await ExecuteRemoveAsync(client, savedRouter, normalized, authBytes, sensitiveValues, cancellationToken).ConfigureAwait(false);
            return result with
            {
                RouterId = savedRouter.Id,
                RouterName = savedRouter.Name,
                Attempts = attempts,
                LastAttemptAt = now,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FailedResult(savedRouter, attempts, "Router call timed out after 10 seconds.", now);
        }
        catch (HttpRequestException ex) when (transportState.CertificatePinMismatch || ContainsAuthenticationException(ex))
        {
            return FailedResult(
                savedRouter,
                attempts,
                transportState.CertificatePinMismatch ? "Router TLS certificate pin mismatch." : "Router TLS connection failed.",
                now);
        }
        catch (HttpRequestException)
        {
            return FailedResult(savedRouter, attempts, "Router connection failed.", now);
        }
        catch (RouterCredentialProtectionException)
        {
            return FailedResult(savedRouter, attempts, "Router credential could not be decrypted.", now);
        }
        catch (InvalidOperationException ex)
        {
            return FailedResult(savedRouter, attempts, OneLine(ex.Message, MaxDetailChars), now);
        }
        finally
        {
            Array.Clear(authBytes);
        }
    }

    private static async Task<MikroTikRouterActionResult> ExecuteBanAsync(
        HttpClient client,
        MikroTikRouter router,
        NormalizedAction normalized,
        byte[] authBytes,
        IReadOnlyList<string> sensitiveValues,
        CancellationToken cancellationToken)
    {
        var body = BanBody(normalized);
        using var request = AuthorizedRequest(HttpMethod.Put, AddressListUrl(router), authBytes);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var responseBody = await ReadBodyPrefixTextAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return AppliedResult("Ban applied.");
        }

        if (response.StatusCode == HttpStatusCode.BadRequest
            && responseBody.Contains("already have such entry", StringComparison.OrdinalIgnoreCase))
        {
            return AppliedResult("Router reported an existing duplicate entry; treated as applied.");
        }

        return FailedResult(WithBodyDetail(
            $"Router returned HTTP {(int)response.StatusCode} {OneLine(response.ReasonPhrase, 80)}.".Trim(),
            responseBody,
            sensitiveValues));
    }

    private static async Task<MikroTikRouterActionResult> ExecuteRemoveAsync(
        HttpClient client,
        MikroTikRouter router,
        NormalizedAction normalized,
        byte[] authBytes,
        IReadOnlyList<string> sensitiveValues,
        CancellationToken cancellationToken)
    {
        using var get = AuthorizedRequest(HttpMethod.Get, AddressListQueryUrl(router, normalized.Ip), authBytes);
        using var getResponse = await client.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var getBody = await ReadBodyPrefixTextAsync(getResponse.Content, cancellationToken).ConfigureAwait(false);
        if (!getResponse.IsSuccessStatusCode)
        {
            return FailedResult(WithBodyDetail(
                $"Router lookup returned HTTP {(int)getResponse.StatusCode} {OneLine(getResponse.ReasonPhrase, 80)}.".Trim(),
                getBody,
                sensitiveValues));
        }

        List<string> ids;
        int returnedEntries;
        try
        {
            (ids, returnedEntries) = ExtractRemovableEntryIds(getBody, normalized.Ip);
        }
        catch (JsonException)
        {
            return FailedResult("Router lookup response was not valid JSON.");
        }

        if (returnedEntries == 0)
        {
            return AppliedResult("No matching ban entries found; treated as applied.");
        }

        if (ids.Count == 0)
        {
            // Entries came back, but none was verifiably ours.  A failed
            // server-side filter must never widen the deletion scope, so
            // refuse instead of deleting.
            return FailedResult(
                $"Router lookup returned {returnedEntries.ToString(CultureInfo.InvariantCulture)} entries, but none matched list '{AddressListName}' and address {normalized.Ip} with a recognized id; refusing to delete.");
        }

        var removed = 0;
        foreach (var id in ids)
        {
            using var delete = AuthorizedRequest(HttpMethod.Delete, AddressListEntryUrl(router, id), authBytes);
            using var deleteResponse = await client.SendAsync(delete, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            var deleteBody = await ReadBodyPrefixTextAsync(deleteResponse.Content, cancellationToken).ConfigureAwait(false);
            if (!deleteResponse.IsSuccessStatusCode)
            {
                return FailedResult(WithBodyDetail(
                    $"Router delete returned HTTP {(int)deleteResponse.StatusCode} {OneLine(deleteResponse.ReasonPhrase, 80)}.".Trim(),
                    deleteBody,
                    sensitiveValues));
            }

            removed++;
        }

        return AppliedResult(
            $"Removed {removed.ToString(CultureInfo.InvariantCulture)} matching ban {(removed == 1 ? "entry" : "entries")}.");
    }

    private static HttpRequestMessage AuthorizedRequest(HttpMethod method, string url, byte[] authBytes)
    {
        var request = new HttpRequestMessage(method, new Uri(url));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
        return request;
    }

    private static ActionStatus DetermineStatus(IReadOnlyCollection<MikroTikRouterActionResult> results)
    {
        if (results.All(IsAppliedResult))
        {
            return ActionStatus.Succeeded;
        }

        return results.Any(result =>
            string.Equals(result.Status, ResultStatuses.Failed, StringComparison.Ordinal)
            && result.Attempts < MaxAttemptsPerRouter)
            ? ActionStatus.Pending
            : ActionStatus.Failed;
    }

    private async ValueTask<ActiveBan?> ApplyDesiredStateChangeAsync(
        ActionRecord actionRecord,
        NormalizedAction normalized,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (normalized.OperationId == RemoveBanOperationId)
        {
            _ = await activeBanStore.RemoveByIpAsync(normalized.Ip, cancellationToken).ConfigureAwait(false);
            return null;
        }

        var existing = await activeBanStore.GetByIpAsync(normalized.Ip, cancellationToken).ConfigureAwait(false);
        // RequestedAt is the stable desired-state creation time.  This keeps
        // retries from extending a ban and keeps delayed older actions from
        // replacing a newer decision for the same IP.
        var actionCreatedAt = actionRecord.RequestedAt == default
            ? now
            : actionRecord.RequestedAt.ToUniversalTime();
        if (existing is not null
            && (existing.ActionId == actionRecord.Id || existing.CreatedAt > actionCreatedAt))
        {
            return existing;
        }

        var desired = new ActiveBan
        {
            Id = ViegardId.New(),
            Ip = normalized.Ip,
            ExpiresAt = now.Add(normalized.Duration ?? throw new InvalidOperationException("Ban duration is required.")),
            CreatedAt = actionCreatedAt,
            DecisionId = actionRecord.DecisionId,
            ActionId = actionRecord.Id,
        };
        return await activeBanStore.UpsertByIpAsync(desired, cancellationToken).ConfigureAwait(false);
    }

    private static MikroTikRouterActionResult DryRunResult(
        MikroTikRouter router,
        NormalizedAction normalized,
        DateTimeOffset now)
    {
        var savedRouter = MikroTikRouterValidator.NormalizeForSave(router);
        var plannedCalls = normalized.OperationId == BanIpOperationId
            ? new[]
            {
                new PlannedRouterOsCall("PUT", AddressListUrl(savedRouter), BanBody(normalized)),
            }
            : new[]
            {
                new PlannedRouterOsCall("GET", AddressListQueryUrl(savedRouter, normalized.Ip), null),
                new PlannedRouterOsCall("DELETE", $"{AddressListUrl(savedRouter)}/{{id-from-get}}", null),
            };

        return new MikroTikRouterActionResult
        {
            RouterId = savedRouter.Id,
            RouterName = savedRouter.Name,
            Attempts = 0,
            Status = ResultStatuses.DryRun,
            Detail = BoundDetail(JsonSerializer.Serialize(plannedCalls, Json)),
            LastAttemptAt = now,
        };
    }

    private static bool TryNormalizeAction(
        ActionRecord actionRecord,
        out NormalizedAction normalized,
        out string error)
    {
        normalized = default!;
        error = string.Empty;

        if (actionRecord.OperationId == BanIpOperationId)
        {
            if (!TryDeserialize(actionRecord.ParametersJson, out BanParameters? parameters, out error)
                || parameters is null)
            {
                return false;
            }

            if (!TryNormalizeIp(parameters.Ip, out var ip, out error))
            {
                return false;
            }

            if (!TryParseRouterOsDuration(parameters.Timeout, out var duration))
            {
                error = "MikroTik ban timeout must be a RouterOS duration such as 5m, 1h30m, 1d, 7d, or 30d.";
                return false;
            }

            if (duration < MinBanTimeout || duration > MaxBanTimeout)
            {
                error = "MikroTik ban timeout must be between 5 minutes and 30 days.";
                return false;
            }

            var formattedTimeout = FormatRouterOsDuration(duration);
            var comment = $"viegard decision {actionRecord.DecisionId:N}";
            var canonical = new BanActionParameters(ip, formattedTimeout, comment);
            normalized = new NormalizedAction(
                actionRecord.OperationId,
                ip,
                JsonSerializer.Serialize(canonical, Json),
                JsonSerializer.Serialize(new RemoveBanActionParameters(ip), Json),
                formattedTimeout,
                comment,
                duration);
            return true;
        }

        if (actionRecord.OperationId == RemoveBanOperationId)
        {
            if (!TryDeserialize(actionRecord.ParametersJson, out RemoveBanParameters? parameters, out error)
                || parameters is null)
            {
                return false;
            }

            if (!TryNormalizeIp(parameters.Ip, out var ip, out error))
            {
                return false;
            }

            normalized = new NormalizedAction(
                actionRecord.OperationId,
                ip,
                JsonSerializer.Serialize(new RemoveBanActionParameters(ip), Json),
                null,
                null,
                null,
                null);
            return true;
        }

        error = $"MikroTik operation '{actionRecord.OperationId}' is not supported.";
        return false;
    }

    private static bool TryDeserialize<T>(string? json, out T? value, out string error)
    {
        value = default;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Action parameters are required.";
            return false;
        }

        try
        {
            value = JsonSerializer.Deserialize<T>(json, Json);
            if (value is null)
            {
                error = "Action parameters were empty.";
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            error = "Action parameters were not valid JSON.";
            return false;
        }
    }

    private static bool TryNormalizeIp(string? value, out string ip, out string error)
    {
        ip = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value)
            || !IPAddress.TryParse(value.Trim(), out var parsed))
        {
            error = "MikroTik action parameter 'ip' must be a valid IPv4 or IPv6 address.";
            return false;
        }

        ip = (parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4() : parsed).ToString();
        return true;
    }

    private static ActionRecord Failed(
        ActionRecord action,
        string error,
        DateTimeOffset now,
        NormalizedAction? normalized = null) =>
        action with
        {
            ParametersJson = normalized?.ParametersJson ?? action.ParametersJson,
            RollbackJson = normalized?.RollbackJson ?? action.RollbackJson,
            ResultsJson = action.ResultsJson ?? "[]",
            Status = ActionStatus.Failed,
            Error = BoundDetail(error),
            CompletedAt = now,
        };

    private static MikroTikRouterActionResult AppliedResult(string detail) => new()
    {
        RouterId = Guid.Empty,
        RouterName = string.Empty,
        Attempts = 0,
        Status = ResultStatuses.Applied,
        Detail = BoundDetail(detail),
        LastAttemptAt = default,
    };

    private static MikroTikRouterActionResult AppliedWithoutCallResult(
        MikroTikRouter router,
        int attempts,
        string detail,
        DateTimeOffset now) => new()
        {
            RouterId = router.Id,
            RouterName = router.Name,
            Attempts = attempts,
            Status = ResultStatuses.AppliedWithoutCall,
            Detail = BoundDetail(detail),
            LastAttemptAt = now,
        };

    private static MikroTikRouterActionResult FailedResult(string detail) => new()
    {
        RouterId = Guid.Empty,
        RouterName = string.Empty,
        Attempts = 0,
        Status = ResultStatuses.Failed,
        Detail = BoundDetail(detail),
        LastAttemptAt = default,
    };

    private static MikroTikRouterActionResult FailedResult(
        MikroTikRouter router,
        int attempts,
        string detail,
        DateTimeOffset now) => new()
        {
            RouterId = router.Id,
            RouterName = router.Name,
            Attempts = attempts,
            Status = ResultStatuses.Failed,
            Detail = BoundDetail(detail),
            LastAttemptAt = now,
        };

    private static IReadOnlyDictionary<Guid, MikroTikRouterActionResult> ParseResults(string? resultsJson)
    {
        if (string.IsNullOrWhiteSpace(resultsJson))
        {
            return new Dictionary<Guid, MikroTikRouterActionResult>();
        }

        try
        {
            var results = JsonSerializer.Deserialize<List<MikroTikRouterActionResult>>(resultsJson, Json) ?? [];
            return results
                .Where(result => result.RouterId != Guid.Empty)
                .GroupBy(result => result.RouterId)
                .ToDictionary(group => group.Key, group => group.Last());
        }
        catch (JsonException)
        {
            return new Dictionary<Guid, MikroTikRouterActionResult>();
        }
    }

    private static string SerializeResults(IReadOnlyCollection<MikroTikRouterActionResult> results) =>
        JsonSerializer.Serialize(results, Json);

    private static string BanBody(NormalizedAction normalized) =>
        JsonSerializer.Serialize(
            new
            {
                list = AddressListName,
                address = normalized.Ip,
                timeout = normalized.TimeoutText,
                comment = normalized.Comment,
            },
            Json);

    private static string AddressListUrl(MikroTikRouter router) =>
        MikroTikAddressListClient.AddressListUrl(router);

    private static string AddressListQueryUrl(MikroTikRouter router, string ip) =>
        $"{MikroTikAddressListClient.AddressListQueryUrl(router, AddressListName)}&address={Uri.EscapeDataString(ip)}";

    private static string AddressListEntryUrl(MikroTikRouter router, string id) =>
        MikroTikAddressListClient.AddressListEntryUrl(router, id);

    /// <summary>RouterOS .id values are an asterisk followed by hex digits.</summary>
    public static bool IsValidEntryId([NotNullWhen(true)] string? id) =>
        MikroTikAddressListClient.IsValidEntryId(id);

    private static (List<string> Ids, int ReturnedEntries) ExtractRemovableEntryIds(string json, string targetIp)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Router lookup response was not a JSON array.");
        }

        // Trust nothing about server-side filtering: an entry is removable
        // only when it verifiably belongs to the Viegard-owned list, names
        // the requested address, and carries a well-formed id.  Anything
        // else is excluded so a broken filter can never widen the deletion
        // scope to entries Viegard does not own.
        var ids = new List<string>();
        var returnedEntries = 0;
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            returnedEntries++;
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty(".id", out var idProperty)
                || idProperty.ValueKind != JsonValueKind.String
                || !IsValidEntryId(idProperty.GetString()))
            {
                continue;
            }

            if (!entry.TryGetProperty("list", out var listProperty)
                || listProperty.ValueKind != JsonValueKind.String
                || !string.Equals(listProperty.GetString(), AddressListName, StringComparison.Ordinal))
            {
                continue;
            }

            if (!entry.TryGetProperty("address", out var addressProperty)
                || addressProperty.ValueKind != JsonValueKind.String
                || !AddressMatches(addressProperty.GetString(), targetIp))
            {
                continue;
            }

            ids.Add(idProperty.GetString()!);
        }

        return (ids, returnedEntries);
    }

    private static bool AddressMatches(string? entryAddress, string targetIp) =>
        !string.IsNullOrWhiteSpace(entryAddress)
        && IPAddress.TryParse(entryAddress.Trim(), out var entryParsed)
        && IPAddress.TryParse(targetIp, out var targetParsed)
        && entryParsed.Equals(targetParsed);

    private static async ValueTask<string> ReadBodyPrefixTextAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[MaxBodyBytes + 1];
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

        if (offset > MaxBodyBytes)
        {
            throw new InvalidOperationException("Router response exceeded the 32768 byte action limit.");
        }

        return Encoding.UTF8.GetString(buffer, 0, offset);
    }

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

    private static string OneLine(string? value, int maxChars)
    {
        var sanitized = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
        return sanitized.Length <= maxChars ? sanitized : sanitized[..maxChars];
    }

    private static string WithBodyDetail(string detail, string responseBody, IReadOnlyList<string> sensitiveValues)
    {
        // The response body carries the router's actual objection (for
        // example which id it rejected); without it a bare status code has
        // already cost a full diagnosis round-trip.  Bodies can echo
        // credential material, so known sensitive values are redacted
        // before anything is recorded.
        var snippet = OneLine(RedactSensitiveValues(responseBody, sensitiveValues), 160).Trim();
        return snippet.Length == 0 ? detail : $"{detail}  Router response: {snippet}";
    }

    public static string RedactSensitiveValues(string text, IReadOnlyList<string> sensitiveValues)
    {
        var result = text;
        foreach (var value in sensitiveValues)
        {
            if (!string.IsNullOrEmpty(value))
            {
                result = result.Replace(value, "[redacted]", StringComparison.Ordinal);
            }
        }

        return result;
    }

    private static string BoundDetail(string value) => OneLine(value, MaxDetailChars);

    private static bool IsAppliedResult(MikroTikRouterActionResult result) =>
        string.Equals(result.Status, ResultStatuses.Applied, StringComparison.Ordinal)
        || string.Equals(result.Status, ResultStatuses.AppliedWithoutCall, StringComparison.Ordinal);

    private static void AppendComponent(StringBuilder builder, long value, char unit)
    {
        if (value > 0)
        {
            builder.Append(value.ToString(CultureInfo.InvariantCulture));
            builder.Append(unit);
        }
    }

    private static class ResultStatuses
    {
        public const string Applied = "applied";
        public const string AppliedWithoutCall = "applied-without-call";
        public const string Failed = "failed";
        public const string DryRun = "dry-run";
    }

    private sealed record BanParameters(string? Ip, string? Timeout);

    private sealed record RemoveBanParameters(string? Ip);

    private sealed record BanActionParameters(string Ip, string Timeout, string Comment);

    private sealed record RemoveBanActionParameters(string Ip);

    private sealed record NormalizedAction(
        string OperationId,
        string Ip,
        string ParametersJson,
        string? RollbackJson,
        string? TimeoutText,
        string? Comment,
        TimeSpan? Duration);

    private sealed record PlannedRouterOsCall(string Method, string Url, string? Body);
}
