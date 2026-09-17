using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Viegard.Application.Configuration;
using Viegard.Application.Logging;
using Viegard.PipelineHost.Configuration;

namespace Viegard.PipelineHost.Workers;

internal sealed partial class HostUpgradeAgentWorker(
    IOptions<HostUpgradeAgentOptions> options,
    IHostUpgradeCommandStore store,
    IHostUpgradeAgentLauncher launcher,
    TimeProvider timeProvider,
    ILogger<HostUpgradeAgentWorker> logger) : BackgroundService
{
    private const string StateFileName = "viegard-host-upgrade-state.json";
    private const string TranscriptFileName = "viegard-host-upgrade-transcript.log";
    private static readonly JsonSerializerOptions StateJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly HostUpgradeAgentOptions _options = options.Value;
    private bool _nonWindowsLogged;

    private string StateDirectory => Path.GetFullPath(launcher.AppBaseDirectory);

    private string StateFilePath => Path.Combine(StateDirectory, StateFileName);

    private string TranscriptPath => Path.Combine(StateDirectory, TranscriptFileName);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        if (!CanRunOnThisHost())
        {
            await IdleUntilStoppedAsync(stoppingToken).ConfigureAwait(false);
            return;
        }

        await RunStartupAsync(stoppingToken).ConfigureAwait(false);
        await PollOnceAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(_options.PollInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    internal async Task RunStartupAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || !CanRunOnThisHost())
        {
            return;
        }

        await ReportCompletionFromStateFileAsync(cancellationToken).ConfigureAwait(false);
        await ReconcileOrphanedRunningAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || !CanRunOnThisHost())
        {
            return;
        }

        if (await HandleExistingStateFileBeforeClaimAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var target = NormalizeConfiguredTarget();
        HostUpgradeCommand? command = null;
        try
        {
            command = await store.ClaimNextPendingAsync(target, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not claim a host upgrade command for target {Target}.", LogSanitizer.Sanitize(target));
            return;
        }

        if (command is null)
        {
            return;
        }

        await LaunchClaimedCommandAsync(command, cancellationToken).ConfigureAwait(false);
    }

    internal static string ResolveCloneRoot(string satelliteScriptPath)
    {
        var scriptFullPath = Path.GetFullPath(satelliteScriptPath);
        var current = File.Exists(scriptFullPath)
            ? new FileInfo(scriptFullPath).Directory
            : new DirectoryInfo(Path.GetDirectoryName(scriptFullPath) ?? Directory.GetCurrentDirectory());

        for (var candidate = current; candidate is not null; candidate = candidate.Parent)
        {
            if (Directory.Exists(Path.Combine(candidate.FullName, ".git")))
            {
                return candidate.FullName;
            }
        }

        if (current?.Name.Equals("windows", StringComparison.OrdinalIgnoreCase) == true
            && current.Parent?.Name.Equals("deploy", StringComparison.OrdinalIgnoreCase) == true
            && current.Parent.Parent is { } repositoryRoot)
        {
            return repositoryRoot.FullName;
        }

        return current?.FullName ?? Directory.GetCurrentDirectory();
    }

    private async Task LaunchClaimedCommandAsync(HostUpgradeCommand command, CancellationToken cancellationToken)
    {
        var target = NormalizeConfiguredTarget();
        var cloneRoot = TryResolveCloneRoot();
        var cloneHeadBefore = cloneRoot is null
            ? null
            : await TryGetCloneHeadAsync(cloneRoot, cancellationToken).ConfigureAwait(false);
        if (!await TryWriteStateFileAsync(command.Id, cloneHeadBefore, cancellationToken).ConfigureAwait(false))
        {
            await CompleteFailedAsync(
                command.Id,
                "Could not write the local upgrade state file.  The satellite upgrade was not launched.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        logger.LogInformation(
            "Claimed host upgrade command {CommandId} for satellite target {Target}.  Triggering scheduled task {TaskName}.",
            command.Id,
            LogSanitizer.Sanitize(target),
            LogSanitizer.Sanitize(_options.ScheduledTaskName));

        var scheduledResult = await TryRunScheduledTaskAsync(cancellationToken).ConfigureAwait(false);
        if (scheduledResult.Succeeded)
        {
            logger.LogInformation(
                "Triggered scheduled task {TaskName} for host upgrade command {CommandId}.",
                LogSanitizer.Sanitize(_options.ScheduledTaskName),
                command.Id);
            return;
        }

        logger.LogWarning(
            "Scheduled task {TaskName} did not start for host upgrade command {CommandId}.  Falling back to detached PowerShell launch.  Detail: {Detail}",
            LogSanitizer.Sanitize(_options.ScheduledTaskName),
            command.Id,
            LogSanitizer.Sanitize(SanitizeDetail(scheduledResult.Output)));

        var detachedResult = await TryLaunchDetachedUpgradeAsync(cancellationToken).ConfigureAwait(false);
        if (detachedResult.Succeeded)
        {
            logger.LogInformation(
                "Started detached fallback upgrade process for host upgrade command {CommandId}.  Transcript: {TranscriptPath}",
                command.Id,
                LogSanitizer.Sanitize(TranscriptPath));
            return;
        }

        var detail = BuildLaunchFailureDetail(scheduledResult, detachedResult);
        await CompleteFailedAsync(command.Id, detail, cancellationToken).ConfigureAwait(false);
        DeleteFileIfExists(StateFilePath);
        DeleteFileIfExists(TranscriptPath);
    }

    private async Task ReportCompletionFromStateFileAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(StateFilePath))
        {
            return;
        }

        var state = await TryReadStateFileAsync(cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return;
        }

        if (state.CommandId == Guid.Empty)
        {
            logger.LogWarning(
                "Local host upgrade state file {StateFilePath} did not contain a command id.",
                LogSanitizer.Sanitize(StateFilePath));
            DeleteFileIfExists(StateFilePath);
            return;
        }

        var cloneRoot = TryResolveCloneRoot();
        var cloneHead = cloneRoot is null
            ? null
            : await TryGetCloneHeadAsync(cloneRoot, cancellationToken).ConfigureAwait(false);
        var deployedCommit = TryReadDeployedCommitMarker();
        var succeeded = !string.IsNullOrWhiteSpace(deployedCommit)
            && !string.IsNullOrWhiteSpace(cloneHead)
            && deployedCommit.Equals(cloneHead, StringComparison.OrdinalIgnoreCase);
        var detail = BuildCompletionDetail(state, deployedCommit, cloneHead, succeeded);

        try
        {
            var completed = await store
                .CompleteAsync(state.CommandId, succeeded, detail, cancellationToken)
                .ConfigureAwait(false);
            if (completed is null)
            {
                logger.LogWarning(
                    "Host upgrade command {CommandId} from local state file was not Running in the command store.",
                    state.CommandId);
            }
            else
            {
                logger.LogInformation(
                    "Reported host upgrade command {CommandId} as {Status}.",
                    state.CommandId,
                    completed.Status);
            }

            DeleteFileIfExists(StateFilePath);
            DeleteFileIfExists(TranscriptPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Could not report completion for host upgrade command {CommandId}.  Keeping local state for the next startup.",
                state.CommandId);
        }
    }

    private async Task ReconcileOrphanedRunningAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(StateFilePath))
        {
            return;
        }

        var target = NormalizeConfiguredTarget();
        IReadOnlyList<HostUpgradeCommand> commands;
        try
        {
            commands = await store
                .ListRecentAsync(target, HostUpgradeCommandPolicy.MaxRecentLimit, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not reconcile running host upgrade commands for target {Target}.", LogSanitizer.Sanitize(target));
            return;
        }

        foreach (var command in commands.Where(command => command.Status == HostUpgradeCommandStatus.Running))
        {
            var detail = "The satellite upgrade agent restarted without a local state file for this command.  Marking the previously running command as failed.";
            await CompleteFailedAsync(command.Id, detail, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<bool> HandleExistingStateFileBeforeClaimAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(StateFilePath))
        {
            return false;
        }

        var state = await TryReadStateFileAsync(cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return true;
        }

        if (state.CommandId == Guid.Empty)
        {
            logger.LogWarning(
                "Local host upgrade state file {StateFilePath} did not contain a command id.",
                LogSanitizer.Sanitize(StateFilePath));
            DeleteFileIfExists(StateFilePath);
            return true;
        }

        var stateAge = timeProvider.GetUtcNow() - state.ClaimedAt.ToUniversalTime();
        if (stateAge < _options.StuckStateGracePeriod)
        {
            logger.LogDebug(
                "Local host upgrade state file for command {CommandId} is {StateAge} old, within the configured grace period {GracePeriod}; skipping claim.",
                state.CommandId,
                stateAge,
                _options.StuckStateGracePeriod);
            return true;
        }

        logger.LogWarning(
            "Local host upgrade state file for command {CommandId} is {StateAge} old, exceeding the configured grace period {GracePeriod}.  Reporting completion before polling for another command.",
            state.CommandId,
            stateAge,
            _options.StuckStateGracePeriod);
        await ReportCompletionFromStateFileAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async ValueTask<bool> TryWriteStateFileAsync(
        Guid commandId,
        string? cloneHeadBefore,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(StateDirectory);
            var state = new HostUpgradeAgentState
            {
                CommandId = commandId,
                ClaimedAt = timeProvider.GetUtcNow(),
                CloneHeadBefore = cloneHeadBefore,
            };
            var raw = JsonSerializer.Serialize(state, StateJsonOptions);
            await File.WriteAllTextAsync(StateFilePath, raw, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not write host upgrade state file {StateFilePath}.", LogSanitizer.Sanitize(StateFilePath));
            return false;
        }
    }

    private async ValueTask<HostUpgradeAgentState?> TryReadStateFileAsync(CancellationToken cancellationToken)
    {
        try
        {
            var raw = await File.ReadAllTextAsync(StateFilePath, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<HostUpgradeAgentState>(raw, StateJsonOptions);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read local host upgrade state file {StateFilePath}.", LogSanitizer.Sanitize(StateFilePath));
            return null;
        }
    }

    private async ValueTask<string?> TryGetCloneHeadAsync(string cloneRoot, CancellationToken cancellationToken)
    {
        try
        {
            return await launcher.GetCloneHeadAsync(cloneRoot, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read clone HEAD from {CloneRoot}.", LogSanitizer.Sanitize(cloneRoot));
            return null;
        }
    }

    private string? TryResolveCloneRoot()
    {
        try
        {
            return ResolveCloneRoot(_options.SatelliteScriptPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Could not resolve the satellite clone root from script path {SatelliteScriptPath}.",
                LogSanitizer.Sanitize(_options.SatelliteScriptPath));
            return null;
        }
    }

    private async ValueTask<HostUpgradeAgentProcessResult> TryRunScheduledTaskAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await launcher.RunScheduledTaskAsync(_options.ScheduledTaskName, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new HostUpgradeAgentProcessResult(-1, ex.Message);
        }
    }

    private async ValueTask<HostUpgradeAgentProcessResult> TryLaunchDetachedUpgradeAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await launcher
                .LaunchDetachedUpgradeAsync(_options.SatelliteScriptPath, _options.ClientName, TranscriptPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new HostUpgradeAgentProcessResult(-1, ex.Message);
        }
    }

    private async Task CompleteFailedAsync(Guid commandId, string detail, CancellationToken cancellationToken)
    {
        try
        {
            var completed = await store
                .CompleteAsync(commandId, succeeded: false, LimitDetail(SanitizeDetail(detail)), cancellationToken)
                .ConfigureAwait(false);
            if (completed is null)
            {
                logger.LogWarning("Host upgrade command {CommandId} could not be marked failed because it was not Running.", commandId);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not mark host upgrade command {CommandId} as failed.", commandId);
        }
    }

    private string? TryReadDeployedCommitMarker()
    {
        var markerPath = Path.Combine(StateDirectory, ".deployed-commit");
        try
        {
            return File.Exists(markerPath)
                ? File.ReadAllText(markerPath).Trim()
                : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read deployed commit marker {MarkerPath}.", LogSanitizer.Sanitize(markerPath));
            return null;
        }
    }

    private string BuildCompletionDetail(
        HostUpgradeAgentState state,
        string? deployedCommit,
        string? cloneHead,
        bool succeeded)
    {
        var beforeShort = ShortCommit(state.CloneHeadBefore);
        var afterShort = ShortCommit(cloneHead);
        var markerShort = ShortCommit(deployedCommit);
        string prefix;
        if (succeeded)
        {
            prefix = string.Equals(state.CloneHeadBefore, cloneHead, StringComparison.OrdinalIgnoreCase)
                ? $"Upgrade completed: {beforeShort}..{afterShort}.  Deployed binary already matched clone HEAD."
                : $"Upgrade completed: {beforeShort}..{afterShort}.  Deployed marker matched clone HEAD.";
        }
        else
        {
            prefix = $"Upgrade completion check failed: {beforeShort}..{afterShort}.  Deployed marker {markerShort}; clone HEAD {afterShort}.";
        }

        return AppendTranscriptTail(prefix);
    }

    private string BuildLaunchFailureDetail(
        HostUpgradeAgentProcessResult scheduledResult,
        HostUpgradeAgentProcessResult detachedResult)
    {
        var detail = "Could not launch the satellite upgrade.  Scheduled task exit "
            + scheduledResult.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ": "
            + scheduledResult.Output
            + Environment.NewLine
            + "Detached fallback exit "
            + detachedResult.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ": "
            + detachedResult.Output;
        return LimitDetail(SanitizeDetail(detail));
    }

    private string AppendTranscriptTail(string prefix)
    {
        var cleanPrefix = SanitizeDetail(prefix).Trim();
        var transcript = TryReadTranscript();
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return LimitDetail(cleanPrefix);
        }

        var header = cleanPrefix + Environment.NewLine + "Transcript tail:" + Environment.NewLine;
        var budget = Math.Max(0, HostUpgradeCommandPolicy.MaxDetailLength - header.Length);
        return header + Tail(SanitizeDetail(transcript).Trim(), budget);
    }

    private string? TryReadTranscript()
    {
        try
        {
            return File.Exists(TranscriptPath) ? File.ReadAllText(TranscriptPath) : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read host upgrade transcript {TranscriptPath}.", LogSanitizer.Sanitize(TranscriptPath));
            return null;
        }
    }

    private bool CanRunOnThisHost()
    {
        if (launcher.IsWindows)
        {
            return true;
        }

        if (!_nonWindowsLogged)
        {
            logger.LogError(
                "Host upgrade satellite agent target {Target} is configured, but this host is not Windows.  The agent will idle.",
                LogSanitizer.Sanitize(_options.Target));
            _nonWindowsLogged = true;
        }

        return false;
    }

    private static async Task IdleUntilStoppedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private string NormalizeConfiguredTarget() => HostUpgradeCommandPolicy.NormalizeTarget(_options.Target);

    private static string SanitizeDetail(string? detail)
    {
        if (string.IsNullOrEmpty(detail))
        {
            return string.Empty;
        }

        var withoutAnsi = AnsiEscapeRegex().Replace(detail, string.Empty);
        var output = new char[withoutAnsi.Length];
        var index = 0;
        foreach (var character in withoutAnsi)
        {
            if (!char.IsControl(character) || character is '\r' or '\n' or '\t')
            {
                output[index++] = character;
            }
        }

        return new string(output, 0, index);
    }

    private static string LimitDetail(string detail) => Tail(detail, HostUpgradeCommandPolicy.MaxDetailLength);

    private static string Tail(string text, int maxLength)
    {
        if (maxLength <= 0)
        {
            return string.Empty;
        }

        return text.Length <= maxLength ? text : text[^maxLength..];
    }

    private static string ShortCommit(string? commit) =>
        string.IsNullOrWhiteSpace(commit)
            ? "unknown"
            : commit.Length <= 9
                ? commit
                : commit[..9];

    private static void DeleteFileIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Cleanup failure is non-fatal; a later startup can reconcile state again.
        }
    }

    [GeneratedRegex("\u001B\\[[0-9;]*[A-Za-z]")]
    private static partial Regex AnsiEscapeRegex();

    private sealed record HostUpgradeAgentState
    {
        public Guid CommandId { get; init; }

        public DateTimeOffset ClaimedAt { get; init; }

        public string? CloneHeadBefore { get; init; }
    }
}
