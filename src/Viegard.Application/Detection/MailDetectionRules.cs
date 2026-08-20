using Viegard.Domain.Events;
using Viegard.Domain.Incidents;

namespace Viegard.Application.Detection;

public sealed class ExcessiveLinksMailDetectionRule(DetectionOptions options) : IDetectionRule
{
    public string RuleId => "mail.link-count";

    public IReadOnlyList<EvidenceItem> Evaluate(NormalizedEvent e)
    {
        if (e.Payload is not MailMessageEvent mail)
        {
            return [];
        }

        var links = MailDetectionHelpers.Links(mail);
        var threshold = Math.Max(0, options.MailLinkCountThreshold);
        if (links.Count <= threshold)
        {
            return [];
        }

        var score = Math.Min(0.45, 0.2 + ((links.Count - threshold) * 0.02));
        return
        [
            DetectionText.Evidence(
                RuleId,
                $"message contains {links.Count} extracted links, above the threshold of {threshold}.",
                score,
                e.Id),
        ];
    }
}

public sealed class FromLinkDomainMismatchMailDetectionRule(DetectionOptions options) : IDetectionRule
{
    public string RuleId => "mail.from-link-domain-mismatch";

    public IReadOnlyList<EvidenceItem> Evaluate(NormalizedEvent e)
    {
        if (e.Payload is not MailMessageEvent mail)
        {
            return [];
        }

        var fromDomain = MailDetectionHelpers.DomainFromAddress(MailDetectionHelpers.FirstAddress(mail.From));
        if (fromDomain is null)
        {
            return [];
        }

        var linkDomains = MailDetectionHelpers.Links(mail)
            .Take(Math.Max(1, options.MaxMailLinksToInspect))
            .Select(MailDetectionHelpers.DomainFromLink)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (linkDomains.Count == 0 || linkDomains.Any(d => MailDetectionHelpers.SameRegisteredDomainApproximation(fromDomain, d)))
        {
            return [];
        }

        return
        [
            DetectionText.Evidence(
                RuleId,
                "message links use domains that do not match the From domain.",
                0.2,
                e.Id),
        ];
    }
}

public sealed class ReplyToDomainMismatchMailDetectionRule : IDetectionRule
{
    public string RuleId => "mail.reply-to-domain-mismatch";

    public IReadOnlyList<EvidenceItem> Evaluate(NormalizedEvent e)
    {
        if (e.Payload is not MailMessageEvent mail)
        {
            return [];
        }

        var fromDomain = MailDetectionHelpers.DomainFromAddress(MailDetectionHelpers.FirstAddress(mail.From));
        var replyToDomain = MailDetectionHelpers.DomainFromAddress(MailDetectionHelpers.FirstAddress(mail.ReplyTo));
        if (fromDomain is null
            || replyToDomain is null
            || MailDetectionHelpers.SameRegisteredDomainApproximation(fromDomain, replyToDomain))
        {
            return [];
        }

        return
        [
            DetectionText.Evidence(
                RuleId,
                "Reply-To domain differs from the From domain.",
                0.3,
                e.Id),
        ];
    }
}

public sealed class AttachmentDoubleExtensionMailDetectionRule(DetectionOptions options) : IDetectionRule
{
    public string RuleId => "mail.attachment-double-extension";

    public IReadOnlyList<EvidenceItem> Evaluate(NormalizedEvent e)
    {
        if (e.Payload is not MailMessageEvent mail)
        {
            return [];
        }

        var executableExtensions = MailDetectionHelpers.ExtensionSet(options.ExecutableAttachmentExtensions);
        var prefixExtensions = MailDetectionHelpers.ExtensionSet(options.DoubleExtensionPrefixes);
        var evidence = new List<EvidenceItem>();

        foreach (var attachment in MailDetectionHelpers.Attachments(mail).Take(Math.Max(1, options.MaxAttachmentNamesToInspect)))
        {
            var fileName = MailDetectionHelpers.FileNameOnly(attachment.FileName);
            var finalExtension = MailDetectionHelpers.FinalExtension(fileName);
            var previousExtension = MailDetectionHelpers.PreviousExtension(fileName);

            if (finalExtension is null
                || previousExtension is null
                || !executableExtensions.Contains(finalExtension)
                || (prefixExtensions.Count > 0 && !prefixExtensions.Contains(previousExtension)))
            {
                continue;
            }

            evidence.Add(DetectionText.Evidence(
                RuleId,
                $"attachment has a misleading double extension ending in '{DetectionText.Display(finalExtension)}'.",
                0.55,
                e.Id));
        }

        return DetectionText.Cap(evidence, options);
    }
}

public sealed class ExecutableAttachmentMailDetectionRule(DetectionOptions options) : IDetectionRule
{
    public string RuleId => "mail.executable-attachment";

    public IReadOnlyList<EvidenceItem> Evaluate(NormalizedEvent e)
    {
        if (e.Payload is not MailMessageEvent mail)
        {
            return [];
        }

        var executableExtensions = MailDetectionHelpers.ExtensionSet(options.ExecutableAttachmentExtensions);
        var evidence = new List<EvidenceItem>();

        foreach (var attachment in MailDetectionHelpers.Attachments(mail).Take(Math.Max(1, options.MaxAttachmentNamesToInspect)))
        {
            var extension = MailDetectionHelpers.FinalExtension(MailDetectionHelpers.FileNameOnly(attachment.FileName));
            if (extension is null || !executableExtensions.Contains(extension))
            {
                continue;
            }

            evidence.Add(DetectionText.Evidence(
                RuleId,
                $"attachment has executable extension '{DetectionText.Display(extension)}'.",
                0.65,
                e.Id));
        }

        return DetectionText.Cap(evidence, options);
    }
}

internal static class MailDetectionHelpers
{
    public static IReadOnlyList<string> Links(MailMessageEvent mail) => mail.Links ?? [];

    public static IReadOnlyList<AttachmentInfo> Attachments(MailMessageEvent mail) => mail.Attachments ?? [];

    public static string? FirstAddress(IReadOnlyList<MailAddressInfo>? addresses) =>
        addresses?.FirstOrDefault()?.Address;

    public static string? DomainFromAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        var trimmed = DetectionText.Limit(address, 512).Trim().Trim('<', '>', '"', '\'');
        var at = trimmed.LastIndexOf('@');
        if (at < 0 || at == trimmed.Length - 1)
        {
            return null;
        }

        return NormalizeDomain(trimmed[(at + 1)..]);
    }

    public static string? DomainFromLink(string? link)
    {
        if (string.IsNullOrWhiteSpace(link))
        {
            return null;
        }

        var trimmed = DetectionText.Limit(link, 2048).Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && !Uri.TryCreate("https://" + trimmed, UriKind.Absolute, out uri))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(uri.Host) ? null : NormalizeDomain(uri.Host);
    }

    public static bool SameRegisteredDomainApproximation(string first, string second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase)
        || first.EndsWith("." + second, StringComparison.OrdinalIgnoreCase)
        || second.EndsWith("." + first, StringComparison.OrdinalIgnoreCase);

    public static HashSet<string> ExtensionSet(IEnumerable<string> extensions) =>
        extensions
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static string FileNameOnly(string? fileName)
    {
        var value = DetectionText.Limit(fileName, 512);
        var lastSlash = value.LastIndexOfAny(['/', '\\']);
        return lastSlash >= 0 ? value[(lastSlash + 1)..] : value;
    }

    public static string? FinalExtension(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        if (dot < 0 || dot == fileName.Length - 1)
        {
            return null;
        }

        return fileName[dot..].ToLowerInvariant();
    }

    public static string? PreviousExtension(string fileName)
    {
        var finalDot = fileName.LastIndexOf('.');
        if (finalDot <= 0)
        {
            return null;
        }

        var previousDot = fileName.LastIndexOf('.', finalDot - 1);
        if (previousDot < 0 || previousDot == finalDot - 1)
        {
            return null;
        }

        return fileName[previousDot..finalDot].ToLowerInvariant();
    }

    private static string? NormalizeDomain(string domain)
    {
        var cleaned = DetectionText.Limit(domain, 255).Trim().TrimEnd('.').ToLowerInvariant();
        return cleaned.Length == 0 ? null : cleaned;
    }
}
