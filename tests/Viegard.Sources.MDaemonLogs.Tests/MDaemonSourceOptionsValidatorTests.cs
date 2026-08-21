namespace Viegard.Sources.MDaemonLogs.Tests;

public sealed class MDaemonSourceOptionsValidatorTests
{
    private readonly MDaemonSourceOptionsValidator _validator = new();

    [Fact]
    public void Disabled_empty_options_pass()
    {
        Assert.True(_validator.Validate(null, new MDaemonSourceOptions()).Succeeded);
    }

    [Fact]
    public void Enabled_requires_log_directory_and_files()
    {
        var result = _validator.Validate(null, new MDaemonSourceOptions { Enabled = true });

        Assert.True(result.Failed);
        Assert.Contains("LogDirectory", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("Files", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Valid_enabled_options_pass()
    {
        var options = new MDaemonSourceOptions
        {
            Enabled = true,
            LogDirectory = "C:\\Logs\\MDaemon",
        };
        options.Files.Add(new MDaemonLogFileOptions { Pattern = "DynScrn-*.log", LogKind = "DynamicScreening" });

        Assert.True(_validator.Validate(null, options).Succeeded);
    }

    [Fact]
    public void Unknown_log_kind_fails_even_when_disabled()
    {
        var options = new MDaemonSourceOptions();
        options.Files.Add(new MDaemonLogFileOptions { Pattern = "*.log", LogKind = "NotARealKind" });

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("unknown LogKind", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_positive_poll_interval_fails_when_enabled()
    {
        var options = new MDaemonSourceOptions
        {
            Enabled = true,
            LogDirectory = "C:\\Logs\\MDaemon",
            PollInterval = TimeSpan.Zero,
        };
        options.Files.Add(new MDaemonLogFileOptions { Pattern = "DynScrn-*.log", LogKind = "DynamicScreening" });

        Assert.True(_validator.Validate(null, options).Failed);
    }
}
