using Microsoft.Extensions.Options;
using Viegard.Domain.Events;

namespace Viegard.Sources.MDaemonLogs;

/// <summary>Configuration for the MDaemon flat-file log source (D-0013, D-0025).</summary>
public sealed class MDaemonSourceOptions
{
    public const string SectionName = "Viegard:Sources:MDaemon";

    /// <summary>The MDaemon log source is off unless explicitly enabled.</summary>
    public bool Enabled { get; set; }

    public string LogDirectory { get; set; } = string.Empty;

    public IList<MDaemonLogFileOptions> Files { get; } = [];

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>When false, drops TrustedIP and AccessRefused Dynamic Screening chatter.</summary>
    public bool IncludeNoise { get; set; }

    /// <summary>When false, first sight of an existing file baselines to EOF.</summary>
    public bool IngestExistingOnFirstRun { get; set; }

    /// <summary>
    /// Upper bound on bytes read from one file in one scan.  Oversized
    /// backlogs are consumed across successive scans instead of being
    /// buffered whole (security-audit finding, 2026-08-25).
    /// </summary>
    public int MaxScanBytes { get; set; } = 8_388_608;
}

/// <summary>One MDaemon filename pattern and the log family it represents.</summary>
public sealed class MDaemonLogFileOptions
{
    public string Pattern { get; set; } = string.Empty;

    public string LogKind { get; set; } = string.Empty;
}

/// <summary>Validates MDaemon log source configuration at startup.</summary>
public sealed class MDaemonSourceOptionsValidator : IValidateOptions<MDaemonSourceOptions>
{
    public ValidateOptionsResult Validate(string? name, MDaemonSourceOptions options)
    {
        var failures = new List<string>();

        foreach (var file in options.Files)
        {
            if (string.IsNullOrWhiteSpace(file.LogKind)
                || !TryParseLogKind(file.LogKind, out _))
            {
                failures.Add($"MDaemon: unknown LogKind '{file.LogKind}' for pattern '{file.Pattern}'.");
            }
        }

        if (!options.Enabled)
        {
            return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
        }

        if (string.IsNullOrWhiteSpace(options.LogDirectory))
        {
            failures.Add("MDaemon: LogDirectory is required when the source is enabled.");
        }

        if (options.Files.Count == 0)
        {
            failures.Add("MDaemon: Files must contain at least one pattern when the source is enabled.");
        }

        foreach (var file in options.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Pattern))
            {
                failures.Add("MDaemon: every file entry requires a non-empty Pattern.");
            }
        }

        if (options.PollInterval <= TimeSpan.Zero)
        {
            failures.Add("MDaemon: PollInterval must be positive.");
        }

        if (options.MaxScanBytes < 4096)
        {
            failures.Add("MDaemon: MaxScanBytes must be at least 4096.");
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }

    public static bool TryParseLogKind(string? value, out MDaemonLogKind logKind)
    {
        logKind = default;
        if (string.IsNullOrWhiteSpace(value)
            || !Enum.GetNames<MDaemonLogKind>().Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return Enum.TryParse(value, ignoreCase: true, out logKind);
    }
}
