using Viegard.Domain.Events;
using Viegard.Domain.Incidents;

namespace Viegard.Application.Detection;

public sealed class SensitivePathHttpDetectionRule(DetectionOptions options) : IDetectionRule
{
    public string RuleId => "http.sensitive-path";

    public IReadOnlyList<EvidenceItem> Evaluate(NormalizedEvent e)
    {
        if (e.Payload is not HttpRequestEvent http)
        {
            return [];
        }

        var uri = DetectionText.Limit(http.Uri, options.MaxInputCharsToScan);
        var path = uri.Split(['?', '#'], 2)[0];
        var decodedPath = DetectionText.DecodeRepeated(path);
        var evidence = new List<EvidenceItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var term in DetectionText.ValidTerms(options.SensitivePaths))
        {
            if (seen.Add(term.Value)
                && (path.Contains(term.Value, StringComparison.OrdinalIgnoreCase)
                    || decodedPath.Contains(term.Value, StringComparison.OrdinalIgnoreCase)))
            {
                evidence.Add(DetectionText.Evidence(
                    RuleId,
                    $"sensitive path probe matched '{DetectionText.Display(term.Value)}'.",
                    term.Score,
                    e.Id));
            }
        }

        return DetectionText.Cap(evidence, options);
    }
}

public sealed class PathTraversalHttpDetectionRule(DetectionOptions options) : IDetectionRule
{
    private static readonly string[] RawIndicators =
    [
        "../",
        "..\\",
        "..%2f",
        "..%5c",
        "%2e%2e",
        "%2e.",
        ".%2e",
        "%252e%252e",
        "%2f..",
        "%5c..",
    ];

    public string RuleId => "http.path-traversal";

    public IReadOnlyList<EvidenceItem> Evaluate(NormalizedEvent e)
    {
        if (e.Payload is not HttpRequestEvent http)
        {
            return [];
        }

        var uri = DetectionText.Limit(http.Uri, options.MaxInputCharsToScan);
        var decoded = DetectionText.DecodeRepeated(uri);
        var evidence = new List<EvidenceItem>();

        foreach (var indicator in RawIndicators)
        {
            if (uri.Contains(indicator, StringComparison.OrdinalIgnoreCase))
            {
                evidence.Add(DetectionText.Evidence(
                    RuleId,
                    $"path traversal indicator '{DetectionText.Display(indicator)}' appeared in the URI.",
                    0.9,
                    e.Id));
                break;
            }
        }

        if (decoded.Contains("../", StringComparison.Ordinal)
            || decoded.Contains("..\\", StringComparison.Ordinal))
        {
            evidence.Add(DetectionText.Evidence(
                RuleId,
                "decoded URI contains parent-directory traversal.",
                0.9,
                e.Id));
        }

        return DetectionText.Cap(evidence, options);
    }
}

public sealed class SqlInjectionHttpDetectionRule(DetectionOptions options) : IDetectionRule
{
    private static readonly (string Indicator, string Description, double Score)[] Terms =
    [
        ("union select", "UNION SELECT SQL injection pattern", 0.9),
        ("' or 1=1", "quoted tautology SQL injection pattern", 0.85),
        ("\" or 1=1", "quoted tautology SQL injection pattern", 0.8),
        ("sleep(", "time-delay SQL function", 0.8),
        ("benchmark(", "time-delay SQL function", 0.8),
        ("information_schema", "database metadata table reference", 0.8),
        ("/**/", "SQL comment obfuscation marker", 0.6),
        ("%27", "URL-encoded quote in a SQL injection context", 0.35),
    ];

    public string RuleId => "http.sql-injection";

    public IReadOnlyList<EvidenceItem> Evaluate(NormalizedEvent e)
    {
        if (e.Payload is not HttpRequestEvent http)
        {
            return [];
        }

        var uri = DetectionText.Limit(http.Uri, options.MaxInputCharsToScan);
        var decoded = DetectionText.DecodeRepeated(uri);
        var evidence = new List<EvidenceItem>();

        foreach (var (indicator, description, score) in Terms)
        {
            if (uri.Contains(indicator, StringComparison.OrdinalIgnoreCase)
                || decoded.Contains(indicator, StringComparison.OrdinalIgnoreCase))
            {
                evidence.Add(DetectionText.Evidence(RuleId, $"{description}.", score, e.Id));
            }
        }

        if (ContainsHexLiteral(uri) || ContainsHexLiteral(decoded))
        {
            evidence.Add(DetectionText.Evidence(
                RuleId,
                "SQL-style hexadecimal literal appeared in the URI.",
                0.35,
                e.Id));
        }

        return DetectionText.Cap(evidence, options);
    }

    private static bool ContainsHexLiteral(string value)
    {
        for (var i = 0; i < value.Length - 3; i++)
        {
            if (value[i] != '0' || (value[i + 1] != 'x' && value[i + 1] != 'X'))
            {
                continue;
            }

            if (IsHex(value[i + 2]) && IsHex(value[i + 3]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHex(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}

public sealed class CommandInjectionHttpDetectionRule(DetectionOptions options) : IDetectionRule
{
    private static readonly (string Indicator, string Description, double Score)[] Terms =
    [
        (";", "command separator", 0.45),
        ("|", "pipeline command separator", 0.5),
        ("`", "shell command substitution marker", 0.6),
        ("$(", "shell command substitution marker", 0.65),
        ("/bin/sh", "Unix shell path", 0.8),
        ("wget http", "download command", 0.75),
        ("curl http", "download command", 0.65),
        ("chmod +x", "executable permission change command", 0.75),
        ("%3b", "URL-encoded command separator", 0.45),
        ("%7c", "URL-encoded pipeline command separator", 0.5),
        ("%60", "URL-encoded shell command substitution marker", 0.6),
        ("%24%28", "URL-encoded shell command substitution marker", 0.65),
    ];

    public string RuleId => "http.command-injection";

    public IReadOnlyList<EvidenceItem> Evaluate(NormalizedEvent e)
    {
        if (e.Payload is not HttpRequestEvent http)
        {
            return [];
        }

        var uri = DetectionText.Limit(http.Uri, options.MaxInputCharsToScan);
        var decoded = DetectionText.DecodeRepeated(uri);
        var evidence = new List<EvidenceItem>();

        foreach (var (indicator, description, score) in Terms)
        {
            if (uri.Contains(indicator, StringComparison.OrdinalIgnoreCase)
                || decoded.Contains(indicator, StringComparison.OrdinalIgnoreCase))
            {
                evidence.Add(DetectionText.Evidence(
                    RuleId,
                    $"{description} '{DetectionText.Display(indicator)}' appeared in the URI.",
                    score,
                    e.Id));
            }
        }

        return DetectionText.Cap(evidence, options);
    }
}

public sealed class XssHttpDetectionRule(DetectionOptions options) : IDetectionRule
{
    private static readonly (string Indicator, string Description, double Score)[] Terms =
    [
        ("<script", "script tag indicator", 0.85),
        ("%3cscript", "URL-encoded script tag indicator", 0.85),
        ("javascript:", "JavaScript URL scheme", 0.75),
        ("onerror=", "inline event-handler indicator", 0.7),
        ("onload=", "inline event-handler indicator", 0.7),
    ];

    public string RuleId => "http.xss";

    public IReadOnlyList<EvidenceItem> Evaluate(NormalizedEvent e)
    {
        if (e.Payload is not HttpRequestEvent http)
        {
            return [];
        }

        var uri = DetectionText.Limit(http.Uri, options.MaxInputCharsToScan);
        var decoded = DetectionText.DecodeRepeated(uri);
        var evidence = new List<EvidenceItem>();

        foreach (var (indicator, description, score) in Terms)
        {
            if (uri.Contains(indicator, StringComparison.OrdinalIgnoreCase)
                || decoded.Contains(indicator, StringComparison.OrdinalIgnoreCase))
            {
                evidence.Add(DetectionText.Evidence(RuleId, $"{description}.", score, e.Id));
            }
        }

        return DetectionText.Cap(evidence, options);
    }
}

public sealed class SuspiciousUserAgentHttpDetectionRule(DetectionOptions options) : IDetectionRule
{
    public string RuleId => "http.suspicious-user-agent";

    public IReadOnlyList<EvidenceItem> Evaluate(NormalizedEvent e)
    {
        if (e.Payload is not HttpRequestEvent http)
        {
            return [];
        }

        if (string.IsNullOrWhiteSpace(http.UserAgent))
        {
            return
            [
                DetectionText.Evidence(
                    RuleId,
                    "empty User-Agent header.",
                    options.EmptyUserAgentScore,
                    e.Id),
            ];
        }

        var userAgent = DetectionText.Limit(http.UserAgent, options.MaxInputCharsToScan);
        var evidence = new List<EvidenceItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var term in DetectionText.ValidTerms(options.SuspiciousUserAgents))
        {
            if (seen.Add(term.Value)
                && userAgent.Contains(term.Value, StringComparison.OrdinalIgnoreCase))
            {
                evidence.Add(DetectionText.Evidence(
                    RuleId,
                    $"scanner or automation User-Agent matched '{DetectionText.Display(term.Value)}'.",
                    term.Score,
                    e.Id));
            }
        }

        return DetectionText.Cap(evidence, options);
    }
}

public sealed class UnusualHttpMethodDetectionRule(DetectionOptions options) : IDetectionRule
{
    public string RuleId => "http.unusual-method";

    public IReadOnlyList<EvidenceItem> Evaluate(NormalizedEvent e)
    {
        if (e.Payload is not HttpRequestEvent http)
        {
            return [];
        }

        var method = DetectionText.Limit(http.Method, 64).Trim();
        if (method.Length == 0)
        {
            return [];
        }

        foreach (var term in DetectionText.ValidTerms(options.UnusualHttpMethods))
        {
            if (string.Equals(method, term.Value, StringComparison.OrdinalIgnoreCase))
            {
                return
                [
                    DetectionText.Evidence(
                        RuleId,
                        $"unusual HTTP method '{DetectionText.Display(method)}'.",
                        term.Score,
                        e.Id),
                ];
            }
        }

        if (!options.StandardHttpMethods.Any(m => string.Equals(method, m, StringComparison.OrdinalIgnoreCase)))
        {
            return
            [
                DetectionText.Evidence(
                    RuleId,
                    $"non-standard HTTP method '{DetectionText.Display(method)}'.",
                    options.NonStandardHttpMethodScore,
                    e.Id),
            ];
        }

        return [];
    }
}

public sealed class ErrorStatusHttpDetectionRule(DetectionOptions options) : IDetectionRule
{
    public string RuleId => "http.error-status";

    public IReadOnlyList<EvidenceItem> Evaluate(NormalizedEvent e)
    {
        if (e.Payload is not HttpRequestEvent http || http.StatusCode is null)
        {
            return [];
        }

        var configured = options.ErrorStatusCodes.FirstOrDefault(s => s.StatusCode == http.StatusCode.Value);
        if (configured is null)
        {
            return [];
        }

        return
        [
            DetectionText.Evidence(
                RuleId,
                $"HTTP status {http.StatusCode.Value} is supporting error evidence.",
                configured.Score,
                e.Id),
        ];
    }
}
