using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Viegard.Application.Configuration;

namespace Viegard.Actions.MikroTik;

public sealed class MikroTikAddressListClient(HttpClient client, MikroTikRouter router, byte[] authBytes)
{
    public async Task<MikroTikAddressListReadResult> ReadListAsync(
        string listName,
        int maxBodyBytes,
        CancellationToken cancellationToken = default)
    {
        using var request = AuthorizedRequest(HttpMethod.Get, AddressListQueryUrl(router, listName), authBytes);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var body = await ReadBodyPrefixTextAsync(response.Content, maxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (body.Truncated)
        {
            return new MikroTikAddressListReadResult(body.Text, true);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Router address-list read returned HTTP {(int)response.StatusCode} {OneLine(response.ReasonPhrase, 80)}.".Trim());
        }

        return new MikroTikAddressListReadResult(body.Text, false);
    }

    public async Task<MikroTikAddressListWriteResult> PutAsync(
        string listName,
        string address,
        int maxErrorBodyBytes,
        string? timeout = null,
        string? comment = null,
        CancellationToken cancellationToken = default)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["list"] = listName,
            ["address"] = address,
        };
        if (!string.IsNullOrWhiteSpace(timeout))
        {
            payload["timeout"] = timeout;
        }

        if (!string.IsNullOrWhiteSpace(comment))
        {
            payload["comment"] = comment;
        }

        using var request = AuthorizedRequest(HttpMethod.Put, AddressListUrl(router), authBytes);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var responseBody = await ReadBodyPrefixTextAsync(response.Content, maxErrorBodyBytes, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return MikroTikAddressListWriteResult.Added();
        }

        if (response.StatusCode == HttpStatusCode.BadRequest
            && responseBody.Text.Contains("already have such entry", StringComparison.OrdinalIgnoreCase))
        {
            return MikroTikAddressListWriteResult.AlreadyPresent();
        }

        throw new InvalidOperationException(
            $"Router add returned HTTP {(int)response.StatusCode} {OneLine(response.ReasonPhrase, 80)}.".Trim());
    }

    public async Task<MikroTikAddressListDeleteResult> DeleteAsync(
        string entryId,
        int maxErrorBodyBytes,
        IReadOnlyList<string>? sensitiveValues = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidEntryId(entryId))
        {
            throw new InvalidOperationException("Router entry id was missing or unrecognized.");
        }

        using var request = AuthorizedRequest(HttpMethod.Delete, AddressListEntryUrl(router, entryId), authBytes);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var responseBody = await ReadBodyPrefixTextAsync(response.Content, maxErrorBodyBytes, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return MikroTikAddressListDeleteResult.Removed();
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return MikroTikAddressListDeleteResult.NotFound();
        }

        var detail = $"Router delete returned HTTP {(int)response.StatusCode} {OneLine(response.ReasonPhrase, 80)}.".Trim();
        var snippetSource = sensitiveValues is null
            ? responseBody.Text
            : MikroTikBanActionProvider.RedactSensitiveValues(responseBody.Text, sensitiveValues);
        var snippet = OneLine(snippetSource, 160).Trim();
        throw new InvalidOperationException(snippet.Length == 0 ? detail : $"{detail}  Router response: {snippet}");
    }

    /// <summary>Updates the comment on an existing address-list entry.</summary>
    public async Task<bool> SetCommentAsync(
        string entryId,
        string comment,
        int maxErrorBodyBytes,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidEntryId(entryId))
        {
            throw new InvalidOperationException("Router entry id was missing or unrecognized.");
        }

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["comment"] = comment ?? string.Empty,
        };

        using var request = AuthorizedRequest(HttpMethod.Patch, AddressListEntryUrl(router, entryId), authBytes);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var responseBody = await ReadBodyPrefixTextAsync(response.Content, maxErrorBodyBytes, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        throw new InvalidOperationException(
            $"Router comment update returned HTTP {(int)response.StatusCode} {OneLine(response.ReasonPhrase, 80)}.".Trim());
    }

    public static string AddressListUrl(MikroTikRouter router) =>
        $"{router.BaseUrl.TrimEnd('/')}/rest/ip/firewall/address-list";

    public static string AddressListQueryUrl(MikroTikRouter router, string listName) =>
        $"{AddressListUrl(router)}?list={Uri.EscapeDataString(listName)}";

    public static string AddressListEntryUrl(MikroTikRouter router, string id) =>
        $"{AddressListUrl(router)}/{id}";

    /// <summary>RouterOS .id values are an asterisk followed by hex digits.</summary>
    public static bool IsValidEntryId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id[0] != '*' || id.Length < 2)
        {
            return false;
        }

        for (var index = 1; index < id.Length; index++)
        {
            if (!Uri.IsHexDigit(id[index]))
            {
                return false;
            }
        }

        return true;
    }

    public static IReadOnlyList<MikroTikAddressListEntry> ParseEntries(string body)
    {
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Router address-list response was not a JSON array.");
        }

        var entries = new List<MikroTikAddressListEntry>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            entries.Add(new MikroTikAddressListEntry(
                Id: ReadString(element, ".id") ?? string.Empty,
                ListName: ReadString(element, "list"),
                RawAddress: ReadString(element, "address") ?? string.Empty,
                CanonicalAddress: TryNormalizeCanonicalAddress(ReadString(element, "address"), out var canonical) ? canonical : null,
                Timeout: ReadString(element, "timeout"),
                Comment: ReadString(element, "comment")));
        }

        return entries;
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool TryNormalizeCanonicalAddress(string? value, out string canonical)
    {
        canonical = string.Empty;
        return JetPackDesiredAddressValidator.TryNormalizeAddress(value, out canonical, out _);
    }

    private static HttpRequestMessage AuthorizedRequest(HttpMethod method, string url, byte[] authBytes)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(authBytes));
        return request;
    }

    private static async Task<BodyPrefixText> ReadBodyPrefixTextAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        var truncated = false;
        while (true)
        {
            var remaining = maxBytes + 1 - (int)buffer.Length;
            if (remaining <= 0)
            {
                truncated = true;
                break;
            }

            var read = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, remaining)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, read);
        }

        if (!truncated)
        {
            var extra = await stream.ReadAsync(chunk.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            truncated = extra != 0;
        }

        var bytes = buffer.ToArray();
        var limit = truncated ? Math.Min(bytes.Length, maxBytes) : bytes.Length;
        return new BodyPrefixText(Encoding.UTF8.GetString(bytes, 0, limit), truncated);
    }

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

    private sealed record BodyPrefixText(string Text, bool Truncated);
}

public sealed record MikroTikAddressListEntry(
    string Id,
    string? ListName,
    string RawAddress,
    string? CanonicalAddress,
    string? Timeout,
    string? Comment)
{
    public bool IsInList(string listName) =>
        string.Equals(ListName, listName, StringComparison.Ordinal);
}

public sealed record MikroTikAddressListReadResult(string Body, bool Truncated);

public enum MikroTikAddressListWriteStatus
{
    Added,
    AlreadyPresent,
}

public sealed record MikroTikAddressListWriteResult(MikroTikAddressListWriteStatus Status)
{
    public static MikroTikAddressListWriteResult Added() => new(MikroTikAddressListWriteStatus.Added);

    public static MikroTikAddressListWriteResult AlreadyPresent() => new(MikroTikAddressListWriteStatus.AlreadyPresent);
}

public enum MikroTikAddressListDeleteStatus
{
    Removed,
    NotFound,
}

public sealed record MikroTikAddressListDeleteResult(MikroTikAddressListDeleteStatus Status)
{
    public static MikroTikAddressListDeleteResult Removed() => new(MikroTikAddressListDeleteStatus.Removed);

    public static MikroTikAddressListDeleteResult NotFound() => new(MikroTikAddressListDeleteStatus.NotFound);
}
