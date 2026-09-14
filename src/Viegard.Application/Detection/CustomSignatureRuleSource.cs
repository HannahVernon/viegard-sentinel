using Viegard.Application.Configuration;
using Viegard.Application.Stores;
using Viegard.Domain.Configuration;
using Viegard.Domain.Events;
using Viegard.Domain.Incidents;

namespace Viegard.Application.Detection;

public sealed class CustomSignatureRuleSource(
    ICustomSignatureStore signatureStore,
    DetectionOptions options,
    ICustomSignatureRuleDiagnostics? diagnostics = null) : IDetectionRule
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    private readonly ICustomSignatureRuleDiagnostics _diagnostics = diagnostics ?? NullCustomSignatureRuleDiagnostics.Instance;
    private readonly HashSet<string> _warnedInvalidSignatures = [];
    private CustomSignatureDetectionRule[] _rules = [];

    public string RuleId => "custom.signatures";

    public IReadOnlyList<EvidenceItem> Evaluate(NormalizedEvent e)
    {
        var rules = Volatile.Read(ref _rules);
        if (rules.Length == 0)
        {
            return [];
        }

        var evidence = new List<EvidenceItem>();
        foreach (var rule in rules)
        {
            evidence.AddRange(rule.Evaluate(e));
        }

        return evidence;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var signatures = await signatureStore.ListAsync(cancellationToken).ConfigureAwait(false);
            var next = new List<CustomSignatureDetectionRule>();
            foreach (var signature in signatures)
            {
                if (!signature.Enabled)
                {
                    continue;
                }

                var validation = CustomSignatureValidator.Validate(signature);
                if (!validation.IsValid)
                {
                    WarnInvalidOnce(signature, validation.Errors);
                    continue;
                }

                next.Add(new CustomSignatureDetectionRule(signature, options));
            }

            Interlocked.Exchange(ref _rules, next.ToArray());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _diagnostics.RefreshFailed(ex);
        }
    }

    public async Task RunRefreshLoopAsync(CancellationToken cancellationToken = default)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        var seenVersion = signatureStore.CurrentChangeVersion;

        while (!cancellationToken.IsCancellationRequested)
        {
            seenVersion = await signatureStore.WaitForChangeAsync(seenVersion, RefreshInterval, cancellationToken)
                .ConfigureAwait(false);
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public int RuleCount => Volatile.Read(ref _rules).Length;

    private void WarnInvalidOnce(CustomSignature signature, IReadOnlyList<string> errors)
    {
        var key = $"{signature.Id:N}:{signature.Version}:{string.Join("|", errors)}";
        lock (_warnedInvalidSignatures)
        {
            if (!_warnedInvalidSignatures.Add(key))
            {
                return;
            }
        }

        _diagnostics.InvalidSignatureSkipped(signature, errors);
    }

    private sealed class CustomSignatureDetectionRule(CustomSignature signature, DetectionOptions options) : IDetectionRule
    {
        public string RuleId => $"custom.signature.{signature.Id:N}";

        public IReadOnlyList<EvidenceItem> Evaluate(NormalizedEvent e)
        {
            if (e.Payload is not HttpRequestEvent http)
            {
                return [];
            }

            var targetValue = GetTargetValue(http, signature.Target);
            if (string.IsNullOrEmpty(targetValue))
            {
                return [];
            }

            var scanned = DetectionText.Limit(targetValue, options.MaxInputCharsToScan);
            var matched = signature.MatchType switch
            {
                CustomSignatureMatchType.Contains => scanned.Contains(signature.Pattern, StringComparison.OrdinalIgnoreCase),
                CustomSignatureMatchType.Prefix => scanned.StartsWith(signature.Pattern, StringComparison.OrdinalIgnoreCase),
                _ => false,
            };

            if (!matched)
            {
                return [];
            }

            return
            [
                DetectionText.Evidence(
                    RuleId,
                    $"custom signature '{DetectionText.Display(signature.Name)}' matched {signature.Target} pattern '{DetectionText.Display(signature.Pattern)}' (category {DetectionText.Display(signature.Category)}, severity {signature.Severity}).",
                    signature.EvidenceWeight,
                    e.Id),
            ];
        }

        private static string GetTargetValue(HttpRequestEvent http, CustomSignatureTarget target) => target switch
        {
            CustomSignatureTarget.HttpUri => http.Uri ?? string.Empty,
            CustomSignatureTarget.HttpQuery => QueryFromUri(http.Uri),
            CustomSignatureTarget.HttpUserAgent => http.UserAgent ?? string.Empty,
            CustomSignatureTarget.HttpPath => PathFromUri(http.Uri),
            _ => string.Empty,
        };

        private static string QueryFromUri(string? uri)
        {
            if (string.IsNullOrEmpty(uri))
            {
                return string.Empty;
            }

            var queryStart = uri.IndexOf('?', StringComparison.Ordinal);
            if (queryStart < 0 || queryStart == uri.Length - 1)
            {
                return string.Empty;
            }

            var fragmentStart = uri.IndexOf('#', queryStart + 1);
            return fragmentStart < 0
                ? uri[(queryStart + 1)..]
                : uri[(queryStart + 1)..fragmentStart];
        }

        private static string PathFromUri(string? uri)
        {
            if (string.IsNullOrEmpty(uri))
            {
                return string.Empty;
            }

            var queryStart = uri.IndexOf('?', StringComparison.Ordinal);
            var fragmentStart = uri.IndexOf('#', StringComparison.Ordinal);
            var end = queryStart < 0
                ? fragmentStart < 0 ? uri.Length : fragmentStart
                : fragmentStart < 0 ? queryStart : Math.Min(queryStart, fragmentStart);
            return uri[..end];
        }
    }
}
