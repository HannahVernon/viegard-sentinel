using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Viegard.AdminApi.Auth;
using Viegard.Application.Detection;
using Viegard.Application.Stores;
using Viegard.Domain;
using Viegard.Domain.Configuration;
using Viegard.Domain.Events;

namespace Viegard.AdminApi.Signatures;

public static class AdminSignatureEndpoints
{
    internal const int PreviewEventScanLimit = 10_000;
    private const int PreviewEventPageSize = 200;

    public static void MapAdminSignatureEndpoints(this WebApplication app)
    {
        app.MapPost("/signatures/save", SaveAsync)
            .RequireAuthorization()
            .RequireRateLimiting("auth");
        app.MapPost("/signatures/preview", PreviewAsync)
            .RequireAuthorization()
            .RequireRateLimiting("auth");
        app.MapPost("/signatures/toggle", ToggleAsync)
            .RequireAuthorization()
            .RequireRateLimiting("auth");
        app.MapPost("/signatures/delete", DeleteAsync)
            .RequireAuthorization()
            .RequireRateLimiting("auth");
    }

    private static async Task<IResult> SaveAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        ICustomSignatureStore signatures,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        AdminAuthAuditor authAuditor,
        AdminConfigAuditor configAuditor)
    {
        var form = await ReadFormAsync(context, antiforgery).ConfigureAwait(false);
        var user = await GetCurrentUserAsync(context, users).ConfigureAwait(false);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        if (!await AdminStepUpGate.HasRecentStepUpAsync(context, sessions).ConfigureAwait(false))
        {
            await authAuditor.RecordAsync(
                AdminAuthEventKind.StepUpFailed,
                user.Username,
                context,
                enqueueForCorrelation: true,
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
            return Redirect("/signatures", error: "Step-up verification is required before editing signatures.");
        }

        var input = SignatureFormInput.From(form);
        if (!TryReadSignature(input, user.Username, out var signature, out var error))
        {
            return Results.Redirect(BuildSignatureFormRedirect(input, error: error));
        }

        var before = await signatures.GetAsync(signature.Id, context.RequestAborted).ConfigureAwait(false);
        try
        {
            // D-0029 configuration writes go directly to the runtime config
            // store and publish NOTIFY.  The D-0011 command queue remains for
            // commands that the pipeline must execute, not admin-owned config.
            var saved = await signatures.UpsertAsync(signature, context.RequestAborted).ConfigureAwait(false);
            await configAuditor.RecordSignatureWriteAsync(
                before is null ? "created" : "updated",
                user.Username,
                before,
                saved,
                context.RequestAborted).ConfigureAwait(false);
            return Redirect("/signatures", status: "Signature saved.");
        }
        catch (InvalidOperationException ex)
        {
            return Redirect("/signatures", error: ex.Message);
        }
    }

    internal static async Task<IResult> PreviewAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IEventStore events,
        IAdminUserStore users,
        IOptions<DetectionOptions> detectionOptions)
    {
        var form = await ReadFormAsync(context, antiforgery).ConfigureAwait(false);
        var input = SignatureFormInput.From(form);
        var user = await GetCurrentUserAsync(context, users).ConfigureAwait(false);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        if (!TryReadSignature(input, user.Username, out var signature, out var error))
        {
            return Results.Redirect(BuildSignatureFormRedirect(input, error: error));
        }

        var preview = await ScanPreviewAsync(
            events,
            signature,
            detectionOptions.Value,
            context.RequestAborted).ConfigureAwait(false);
        return Results.Redirect(BuildSignatureFormRedirect(SignatureFormInput.FromSignature(input.IdText, signature), preview));
    }

    private static async Task<IResult> ToggleAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        ICustomSignatureStore signatures,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        AdminAuthAuditor authAuditor,
        AdminConfigAuditor configAuditor)
    {
        var form = await ReadFormAsync(context, antiforgery).ConfigureAwait(false);
        var user = await GetCurrentUserAsync(context, users).ConfigureAwait(false);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        if (!await AdminStepUpGate.HasRecentStepUpAsync(context, sessions).ConfigureAwait(false))
        {
            await authAuditor.RecordAsync(
                AdminAuthEventKind.StepUpFailed,
                user.Username,
                context,
                enqueueForCorrelation: true,
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
            return Redirect("/signatures", error: "Step-up verification is required before changing signatures.");
        }

        if (!Guid.TryParse(form["id"].ToString(), out var id))
        {
            return Redirect("/signatures", error: "Signature not found.");
        }

        var before = await signatures.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
        if (before is null)
        {
            return Redirect("/signatures", error: "Signature not found.");
        }

        var saved = await signatures.UpsertAsync(before with
        {
            Enabled = !before.Enabled,
            UpdatedBy = user.Username,
        }, context.RequestAborted).ConfigureAwait(false);
        await configAuditor.RecordSignatureWriteAsync(
            saved.Enabled ? "enabled" : "disabled",
            user.Username,
            before,
            saved,
            context.RequestAborted).ConfigureAwait(false);
        return Redirect("/signatures", status: saved.Enabled ? "Signature enabled." : "Signature disabled.");
    }

    private static async Task<IResult> DeleteAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        ICustomSignatureStore signatures,
        IAdminUserStore users,
        IAdminSessionStore sessions,
        AdminAuthAuditor authAuditor,
        AdminConfigAuditor configAuditor)
    {
        var form = await ReadFormAsync(context, antiforgery).ConfigureAwait(false);
        var user = await GetCurrentUserAsync(context, users).ConfigureAwait(false);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        if (!await AdminStepUpGate.HasRecentStepUpAsync(context, sessions).ConfigureAwait(false))
        {
            await authAuditor.RecordAsync(
                AdminAuthEventKind.StepUpFailed,
                user.Username,
                context,
                enqueueForCorrelation: true,
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
            return Redirect("/signatures", error: "Step-up verification is required before deleting signatures.");
        }

        if (!Guid.TryParse(form["id"].ToString(), out var id))
        {
            return Redirect("/signatures", error: "Signature not found.");
        }

        var removed = await signatures.DeleteAsync(id, context.RequestAborted).ConfigureAwait(false);
        if (removed is null)
        {
            return Redirect("/signatures", error: "Signature not found.");
        }

        await configAuditor.RecordSignatureWriteAsync(
            "deleted",
            user.Username,
            removed,
            after: null,
            context.RequestAborted).ConfigureAwait(false);
        return Redirect("/signatures", status: "Signature deleted.");
    }

    private static bool TryReadSignature(
        SignatureFormInput input,
        string username,
        out CustomSignature signature,
        out string error)
    {
        signature = new CustomSignature
        {
            Id = ViegardId.New(),
            Name = string.Empty,
            Enabled = false,
            Target = CustomSignatureTarget.HttpUri,
            MatchType = CustomSignatureMatchType.Contains,
            Pattern = string.Empty,
            Category = string.Empty,
            Severity = 0,
            EvidenceWeight = 1.0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = username,
            Version = 0,
        };
        error = string.Empty;

        var idText = input.IdText;
        var id = string.IsNullOrWhiteSpace(idText) ? ViegardId.New() : Guid.Empty;
        if (!string.IsNullOrWhiteSpace(idText) && !Guid.TryParse(idText, out id))
        {
            error = "Signature not found.";
            return false;
        }

        var target = Enum.TryParse<CustomSignatureTarget>(input.Target, ignoreCase: true, out var parsedTarget)
            ? parsedTarget
            : (CustomSignatureTarget)(-1);
        var matchType = input.AdditionalPatterns.Count > 0
            ? CustomSignatureMatchType.ContainsAll
            : Enum.TryParse<CustomSignatureMatchType>(input.MatchType, ignoreCase: true, out var parsedMatch)
            ? parsedMatch
            : (CustomSignatureMatchType)(-1);
        var severity = int.TryParse(input.Severity, out var parsedSeverity)
            ? parsedSeverity
            : -1;
        var evidenceWeight = double.TryParse(
            input.EvidenceWeight,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsedWeight)
            ? parsedWeight
            : double.NaN;

        signature = new CustomSignature
        {
            Id = id,
            Name = input.Name,
            Enabled = input.Enabled,
            Target = target,
            MatchType = matchType,
            Pattern = input.Pattern,
            AdditionalPatterns = input.AdditionalPatterns,
            Category = input.Category,
            Severity = severity,
            EvidenceWeight = evidenceWeight,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = username,
            Version = 0,
        };
        signature = CustomSignatureValidator.Normalize(signature);
        var validation = CustomSignatureValidator.Validate(signature);
        if (validation.IsValid)
        {
            return true;
        }

        error = CustomSignatureValidator.UniformError(validation);
        return false;
    }

    internal static async ValueTask<SignaturePreviewResult> ScanPreviewAsync(
        IEventStore events,
        CustomSignature signature,
        DetectionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(options);

        var matchIds = new List<Guid>();
        var matchCount = 0;
        var scanned = 0;
        DateTimeOffset? newest = null;
        DateTimeOffset? oldest = null;
        Guid? cursor = null;
        var sort = new ListSort<EventSortColumn>(EventSortColumn.Occurred, SortDirection.Desc);
        var capped = false;

        while (scanned < PreviewEventScanLimit)
        {
            var pageSize = Math.Min(PreviewEventPageSize, PreviewEventScanLimit - scanned);
            var page = await events.ListPageAsync(
                cursor,
                pageSize,
                filter: null,
                sort,
                cancellationToken).ConfigureAwait(false);
            if (page.Items.Count == 0)
            {
                break;
            }

            foreach (var candidateEvent in page.Items)
            {
                scanned++;
                newest = newest is null || candidateEvent.OccurredAt > newest.Value
                    ? candidateEvent.OccurredAt
                    : newest;
                oldest = oldest is null || candidateEvent.OccurredAt < oldest.Value
                    ? candidateEvent.OccurredAt
                    : oldest;

                if (CustomSignatureMatcher.Matches(candidateEvent, signature, options))
                {
                    matchCount++;
                    if (matchIds.Count < 10)
                    {
                        matchIds.Add(candidateEvent.Id);
                    }
                }
            }

            if (scanned >= PreviewEventScanLimit)
            {
                capped = page.NextCursor is not null;
                break;
            }

            if (page.NextCursor is null)
            {
                break;
            }

            cursor = page.NextCursor;
        }

        return new SignaturePreviewResult(scanned, matchCount, newest, oldest, matchIds, capped);
    }

    private static string BuildSignatureFormRedirect(SignatureFormInput input, SignaturePreviewResult? preview = null, string? error = null)
    {
        var parameters = input.ToQueryParameters();
        if (!string.IsNullOrWhiteSpace(input.IdText))
        {
            parameters.Add(new("editId", input.IdText));
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            parameters.Add(new("error", error));
        }

        if (preview is not null)
        {
            parameters.Add(new("preview", "1"));
            parameters.Add(new("previewScanned", preview.ScannedCount.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            parameters.Add(new("previewMatchCount", preview.MatchCount.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            parameters.Add(new("previewCapped", preview.Capped ? "true" : "false"));
            parameters.Add(new("previewNewest", preview.NewestOccurredAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
            parameters.Add(new("previewOldest", preview.OldestOccurredAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
            parameters.Add(new("previewMatchIds", string.Join(',', preview.MatchingEventIds.Select(id => id.ToString("D")))));
        }

        return BuildRedirectPath("/signatures#add", parameters);
    }

    private static async Task<IFormCollection> ReadFormAsync(HttpContext context, IAntiforgery antiforgery)
    {
        await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
        return await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
    }

    private static async ValueTask<Viegard.Domain.Admin.AdminUser?> GetCurrentUserAsync(HttpContext context, IAdminUserStore users)
    {
        var userIdClaim = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(userIdClaim, out var userId)
            ? await users.GetByIdAsync(userId, context.RequestAborted).ConfigureAwait(false)
            : null;
    }

    private static IResult Redirect(string path, string? status = null, string? error = null)
    {
        var parameters = new List<KeyValuePair<string, string?>>();
        if (status is not null)
        {
            parameters.Add(new("status", status));
        }
        else if (error is not null)
        {
            parameters.Add(new("error", error));
        }

        return Results.Redirect(BuildRedirectPath(path, parameters));
    }

    private static string BuildRedirectPath(string path, List<KeyValuePair<string, string?>> parameters)
    {
        var query = string.Join(
            '&',
            parameters
                .Where(parameter => parameter.Value is not null)
                .Select(parameter => $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value!)}"));
        if (string.IsNullOrEmpty(query))
        {
            return path;
        }

        var fragmentStart = path.IndexOf('#', StringComparison.Ordinal);
        var basePath = fragmentStart < 0 ? path : path[..fragmentStart];
        var fragment = fragmentStart < 0 ? string.Empty : path[fragmentStart..];
        var separator = basePath.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{basePath}{separator}{query}{fragment}";
    }

    private sealed record SignatureFormInput(
        string IdText,
        string Name,
        string Target,
        string MatchType,
        string Pattern,
        string AdditionalPattern2,
        string AdditionalPattern3,
        IReadOnlyList<string> AdditionalPatterns,
        string Category,
        string Severity,
        string EvidenceWeight,
        bool Enabled)
    {
        private const string AdditionalPattern2Field = "additionalPattern2";
        private const string AdditionalPattern3Field = "additionalPattern3";

        public static SignatureFormInput From(IFormCollection form)
        {
            var additionalPatterns = new List<string>();
            AddAdditionalValues(form, AdditionalPattern2Field, additionalPatterns);
            AddAdditionalValues(form, AdditionalPattern3Field, additionalPatterns);
            foreach (var field in form)
            {
                if (!field.Key.StartsWith("additionalPattern", StringComparison.OrdinalIgnoreCase)
                    || field.Key.Equals(AdditionalPattern2Field, StringComparison.OrdinalIgnoreCase)
                    || field.Key.Equals(AdditionalPattern3Field, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var value in field.Value)
                {
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        additionalPatterns.Add(value);
                    }
                }
            }

            return new SignatureFormInput(
                Field(form, "id"),
                Field(form, "name"),
                Field(form, "target"),
                Field(form, "matchType"),
                Field(form, "pattern"),
                Field(form, AdditionalPattern2Field),
                Field(form, AdditionalPattern3Field),
                additionalPatterns,
                Field(form, "category"),
                Field(form, "severity"),
                Field(form, "evidenceWeight"),
                IsChecked(Field(form, "enabled")));
        }

        public static SignatureFormInput FromSignature(string idText, CustomSignature signature)
        {
            var additionalPatterns = signature.AdditionalPatterns ?? [];
            return new SignatureFormInput(
                idText,
                signature.Name,
                signature.Target.ToString(),
                signature.MatchType.ToString(),
                signature.Pattern,
                additionalPatterns.Count > 0 ? additionalPatterns[0] : string.Empty,
                additionalPatterns.Count > 1 ? additionalPatterns[1] : string.Empty,
                additionalPatterns.ToList(),
                signature.Category,
                signature.Severity.ToString(System.Globalization.CultureInfo.InvariantCulture),
                signature.EvidenceWeight.ToString(System.Globalization.CultureInfo.InvariantCulture),
                signature.Enabled);
        }

        public List<KeyValuePair<string, string?>> ToQueryParameters() =>
        [
            new("form", "1"),
            new("formId", IdText),
            new("formName", Name),
            new("formTarget", Target),
            new("formMatchType", MatchType),
            new("formPattern", Pattern),
            new("formAdditionalPattern2", AdditionalPattern2),
            new("formAdditionalPattern3", AdditionalPattern3),
            new("formCategory", Category),
            new("formSeverity", Severity),
            new("formEvidenceWeight", EvidenceWeight),
            new("formEnabled", Enabled ? "true" : "false"),
        ];

        private static void AddAdditionalValues(IFormCollection form, string fieldName, List<string> values)
        {
            foreach (var value in form[fieldName])
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    values.Add(value);
                }
            }
        }

        private static string Field(IFormCollection form, string name) =>
            form[name].FirstOrDefault() ?? string.Empty;

        private static bool IsChecked(string value) =>
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record SignaturePreviewResult(
    int ScannedCount,
    int MatchCount,
    DateTimeOffset? NewestOccurredAt,
    DateTimeOffset? OldestOccurredAt,
    IReadOnlyList<Guid> MatchingEventIds,
    bool Capped);
