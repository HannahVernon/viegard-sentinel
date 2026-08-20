namespace Viegard.Application.Detection;

public sealed class DetectionOptions
{
    public const string SectionName = "Viegard:Detection";

    public int MaxInputCharsToScan { get; set; } = 8192;

    public int MaxEvidencePerRule { get; set; } = 12;

    public int MailLinkCountThreshold { get; set; } = 10;

    public int MaxMailLinksToInspect { get; set; } = 50;

    public int MaxAttachmentNamesToInspect { get; set; } = 20;

    public IList<ScoredDetectionTerm> SensitivePaths { get; set; } =
    [
        new() { Value = "/.env", Score = 0.95 },
        new() { Value = "/.git/", Score = 0.95 },
        new() { Value = "/wp-login.php", Score = 0.65 },
        new() { Value = "/xmlrpc.php", Score = 0.55 },
        new() { Value = "/vendor/phpunit", Score = 0.9 },
        new() { Value = "/.aws/", Score = 0.95 },
        new() { Value = "/config/", Score = 0.55 },
        new() { Value = "/backup", Score = 0.5 },
        new() { Value = "/phpMyAdmin", Score = 0.65 },
        new() { Value = "/admin", Score = 0.35 },
        new() { Value = "/cgi-bin", Score = 0.45 },
        new() { Value = "/HNAP1", Score = 0.8 },
        new() { Value = "/boaform", Score = 0.8 },
        new() { Value = "/actuator", Score = 0.75 },
        new() { Value = "/.DS_Store", Score = 0.5 },
        new() { Value = "/web.config", Score = 0.85 },
        new() { Value = "/appsettings.json", Score = 0.9 },
    ];

    public IList<ScoredDetectionTerm> SuspiciousUserAgents { get; set; } =
    [
        new() { Value = "zgrab", Score = 0.9 },
        new() { Value = "masscan", Score = 0.9 },
        new() { Value = "nikto", Score = 0.9 },
        new() { Value = "sqlmap", Score = 0.95 },
        new() { Value = "nuclei", Score = 0.85 },
        new() { Value = "gobuster", Score = 0.8 },
        new() { Value = "dirbuster", Score = 0.8 },
        new() { Value = "wpscan", Score = 0.85 },
        new() { Value = "python-requests", Score = 0.45 },
        new() { Value = "Go-http-client", Score = 0.45 },
        new() { Value = "curl", Score = 0.35 },
    ];

    public double EmptyUserAgentScore { get; set; } = 0.25;

    public IList<string> StandardHttpMethods { get; set; } =
    [
        "GET",
        "POST",
        "HEAD",
        "PUT",
        "DELETE",
        "PATCH",
        "OPTIONS",
    ];

    public IList<ScoredDetectionTerm> UnusualHttpMethods { get; set; } =
    [
        new() { Value = "TRACE", Score = 0.65 },
        new() { Value = "TRACK", Score = 0.65 },
        new() { Value = "CONNECT", Score = 0.55 },
        new() { Value = "PROPFIND", Score = 0.45 },
    ];

    public double NonStandardHttpMethodScore { get; set; } = 0.35;

    public IList<StatusCodeDetectionScore> ErrorStatusCodes { get; set; } =
    [
        new() { StatusCode = 400, Score = 0.1 },
        new() { StatusCode = 401, Score = 0.12 },
        new() { StatusCode = 403, Score = 0.14 },
        new() { StatusCode = 404, Score = 0.08 },
    ];

    public IList<string> ExecutableAttachmentExtensions { get; set; } =
    [
        ".exe",
        ".scr",
        ".js",
        ".vbs",
        ".bat",
        ".cmd",
        ".ps1",
    ];

    public IList<string> DoubleExtensionPrefixes { get; set; } =
    [
        ".pdf",
        ".doc",
        ".docx",
        ".xls",
        ".xlsx",
        ".jpg",
        ".jpeg",
        ".png",
        ".txt",
        ".rtf",
        ".zip",
    ];
}

public sealed class ScoredDetectionTerm
{
    public string Value { get; set; } = string.Empty;

    public double Score { get; set; }
}

public sealed class StatusCodeDetectionScore
{
    public int StatusCode { get; set; }

    public double Score { get; set; }
}
