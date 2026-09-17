using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Viegard.Application.Configuration;
using Viegard.Persistence.InMemory;
using Viegard.PipelineHost.Configuration;
using Viegard.PipelineHost.Workers;

namespace Viegard.Application.Tests;

public sealed class HostUpgradeAgentWorkerTests
{
    private const string StateFileName = "viegard-host-upgrade-state.json";
    private const string TranscriptFileName = "viegard-host-upgrade-transcript.log";
    private const string BeforeHead = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string AfterHead = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 22, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Options_validation_rejects_invalid_satellite_agent_configuration()
    {
        var validator = new HostUpgradeAgentOptionsValidator();

        Assert.True(validator.Validate(Options.DefaultName, new HostUpgradeAgentOptions()).Succeeded);
        Assert.False(validator.Validate(Options.DefaultName, new HostUpgradeAgentOptions
        {
            Target = "sat-a",
            SatelliteScriptPath = "C:\\Viegard\\deploy\\windows\\viegard-satellite.ps1",
            StateDirectory = "relative-state",
        }).Succeeded);
        Assert.False(validator.Validate(Options.DefaultName, new HostUpgradeAgentOptions
        {
            Target = "sat-a",
            PollInterval = TimeSpan.FromSeconds(4),
            SatelliteScriptPath = "C:\\Viegard\\deploy\\windows\\viegard-satellite.ps1",
        }).Succeeded);
        Assert.False(validator.Validate(Options.DefaultName, new HostUpgradeAgentOptions
        {
            Target = "sat-a",
            StuckStateGracePeriod = TimeSpan.FromSeconds(59),
            SatelliteScriptPath = "C:\\Viegard\\deploy\\windows\\viegard-satellite.ps1",
        }).Succeeded);
        Assert.False(validator.Validate(Options.DefaultName, new HostUpgradeAgentOptions
        {
            Target = "sat-a",
            StuckStateGracePeriod = TimeSpan.FromMinutes(61),
            SatelliteScriptPath = "C:\\Viegard\\deploy\\windows\\viegard-satellite.ps1",
        }).Succeeded);
        Assert.False(validator.Validate(Options.DefaultName, new HostUpgradeAgentOptions
        {
            Target = "Sat-A",
            SatelliteScriptPath = "C:\\Viegard\\deploy\\windows\\viegard-satellite.ps1",
        }).Succeeded);
        Assert.False(validator.Validate(Options.DefaultName, new HostUpgradeAgentOptions
        {
            Target = HostUpgradeCommandPolicy.DefaultTarget,
            SatelliteScriptPath = "C:\\Viegard\\deploy\\windows\\viegard-satellite.ps1",
        }).Succeeded);
        Assert.False(validator.Validate(Options.DefaultName, new HostUpgradeAgentOptions
        {
            Target = "sat-a",
        }).Succeeded);
        Assert.True(validator.Validate(Options.DefaultName, new HostUpgradeAgentOptions
        {
            Target = "sat-a",
            SatelliteScriptPath = "C:\\Viegard\\deploy\\windows\\viegard-satellite.ps1",
            PollInterval = TimeSpan.FromSeconds(5),
        }).Succeeded);
    }

    [Fact]
    public void Options_default_state_directory_uses_program_data_and_target()
    {
        var options = new HostUpgradeAgentOptions { Target = "sat-a" };

        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Viegard",
            "sat-a");

        Assert.Equal(expected, options.GetStateDirectory());
    }

    [Fact]
    public async Task Poll_with_fresh_state_file_does_not_complete_or_claim()
    {
        var testPaths = CreateTestPaths();
        try
        {
            var time = new FakeTimeProvider(Now);
            var store = new InMemoryHostUpgradeCommandStore(time);
            var requested = await store.RequestAsync("sat-a", "hannah");
            WriteStateFile(testPaths.StateDirectory, requested.Id, claimedAt: Now);
            var launcher = new RecordingLauncher(testPaths.AppDirectory);
            launcher.CloneHeads.Enqueue(AfterHead);
            var worker = CreateWorker(store, launcher, testPaths.ScriptPath, testPaths.StateDirectory, time);

            await worker.PollOnceAsync();

            var command = Assert.Single(await store.ListRecentAsync("sat-a"));
            Assert.Equal(HostUpgradeCommandStatus.Pending, command.Status);
            Assert.Empty(launcher.CloneRootCalls);
            Assert.Empty(launcher.ScheduledTaskCalls);
            Assert.Empty(launcher.DetachedLaunches);
            Assert.True(File.Exists(Path.Combine(testPaths.StateDirectory, StateFileName)));
        }
        finally
        {
            testPaths.Delete();
        }
    }

    [Fact]
    public async Task Poll_with_aged_state_file_reports_success_when_marker_matches_clone_head()
    {
        var testPaths = CreateTestPaths();
        try
        {
            var time = new FakeTimeProvider(Now);
            var store = new InMemoryHostUpgradeCommandStore(time);
            var requested = await store.RequestAsync("sat-a", "hannah");
            Assert.NotNull(await store.ClaimNextPendingAsync("sat-a"));
            WriteStateFile(
                testPaths.StateDirectory,
                requested.Id,
                claimedAt: Now.Subtract(TimeSpan.FromMinutes(11)),
                cloneHeadBefore: AfterHead);
            await File.WriteAllTextAsync(Path.Combine(testPaths.AppDirectory, ".deployed-commit"), AfterHead);
            var launcher = new RecordingLauncher(testPaths.AppDirectory);
            launcher.CloneHeads.Enqueue(AfterHead);
            var worker = CreateWorker(store, launcher, testPaths.ScriptPath, testPaths.StateDirectory, time);

            await worker.PollOnceAsync();

            var command = Assert.Single(await store.ListRecentAsync("sat-a"));
            Assert.Equal(HostUpgradeCommandStatus.Succeeded, command.Status);
            Assert.NotNull(command.Detail);
            Assert.Contains("already matched", command.Detail, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(testPaths.StateDirectory, StateFileName)));
            Assert.False(File.Exists(Path.Combine(testPaths.StateDirectory, TranscriptFileName)));
            Assert.Empty(launcher.ScheduledTaskCalls);
        }
        finally
        {
            testPaths.Delete();
        }
    }

    [Fact]
    public async Task Poll_with_aged_state_file_reports_failure_when_marker_differs_from_clone_head()
    {
        var testPaths = CreateTestPaths();
        try
        {
            var time = new FakeTimeProvider(Now);
            var store = new InMemoryHostUpgradeCommandStore(time);
            var requested = await store.RequestAsync("sat-a", "hannah");
            Assert.NotNull(await store.ClaimNextPendingAsync("sat-a"));
            WriteStateFile(
                testPaths.StateDirectory,
                requested.Id,
                claimedAt: Now.Subtract(TimeSpan.FromMinutes(11)));
            await File.WriteAllTextAsync(Path.Combine(testPaths.AppDirectory, ".deployed-commit"), "cccccccccccccccccccccccccccccccccccccccc");
            var launcher = new RecordingLauncher(testPaths.AppDirectory);
            launcher.CloneHeads.Enqueue(AfterHead);
            var worker = CreateWorker(store, launcher, testPaths.ScriptPath, testPaths.StateDirectory, time);

            await worker.PollOnceAsync();

            var command = Assert.Single(await store.ListRecentAsync("sat-a"));
            Assert.Equal(HostUpgradeCommandStatus.Failed, command.Status);
            Assert.NotNull(command.Detail);
            Assert.Contains("completion check failed", command.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(testPaths.StateDirectory, StateFileName)));
            Assert.Empty(launcher.ScheduledTaskCalls);
        }
        finally
        {
            testPaths.Delete();
        }
    }

    [Fact]
    public async Task Poll_after_aged_state_completion_claims_normally_on_next_poll()
    {
        var testPaths = CreateTestPaths();
        try
        {
            var time = new FakeTimeProvider(Now);
            var store = new InMemoryHostUpgradeCommandStore(time);
            var completedRequest = await store.RequestAsync("sat-a", "hannah");
            Assert.NotNull(await store.ClaimNextPendingAsync("sat-a"));
            WriteStateFile(
                testPaths.StateDirectory,
                completedRequest.Id,
                claimedAt: Now.Subtract(TimeSpan.FromMinutes(11)),
                cloneHeadBefore: AfterHead);
            await File.WriteAllTextAsync(Path.Combine(testPaths.AppDirectory, ".deployed-commit"), AfterHead);
            var launcher = new RecordingLauncher(testPaths.AppDirectory);
            launcher.CloneHeads.Enqueue(AfterHead);
            launcher.CloneHeads.Enqueue(BeforeHead);
            launcher.ScheduledResults.Enqueue(new HostUpgradeAgentProcessResult(0, "task started"));
            var worker = CreateWorker(store, launcher, testPaths.ScriptPath, testPaths.StateDirectory, time);

            await worker.PollOnceAsync();
            Assert.Empty(launcher.ScheduledTaskCalls);

            time.Advance(HostUpgradeCommandPolicy.Cooldown.Add(TimeSpan.FromSeconds(1)));
            var nextRequest = await store.RequestAsync("sat-a", "hannah");
            await worker.PollOnceAsync();

            var commands = await store.ListRecentAsync("sat-a", limit: 10);
            var claimed = Assert.Single(commands, command => command.Id == nextRequest.Id);
            Assert.Equal(HostUpgradeCommandStatus.Running, claimed.Status);
            Assert.Equal(["ViegardSatelliteMDaemonAutoUpgrade"], launcher.ScheduledTaskCalls);
        }
        finally
        {
            testPaths.Delete();
        }
    }

    [Fact]
    public async Task Poll_claims_command_writes_state_and_triggers_scheduled_task()
    {
        var testPaths = CreateTestPaths();
        try
        {
            var time = new FakeTimeProvider(Now);
            var store = new InMemoryHostUpgradeCommandStore(time);
            var requested = await store.RequestAsync("sat-a", "hannah");
            var launcher = new RecordingLauncher(testPaths.AppDirectory);
            launcher.CloneHeads.Enqueue(BeforeHead);
            launcher.ScheduledResults.Enqueue(new HostUpgradeAgentProcessResult(0, "task started"));
            var worker = CreateWorker(store, launcher, testPaths.ScriptPath, testPaths.StateDirectory, time);

            await worker.PollOnceAsync();

            var statePath = Path.Combine(testPaths.StateDirectory, StateFileName);
            Assert.True(File.Exists(statePath));
            Assert.False(File.Exists(Path.Combine(testPaths.AppDirectory, StateFileName)));
            using var stateJson = JsonDocument.Parse(await File.ReadAllTextAsync(statePath));
            Assert.Equal(requested.Id, stateJson.RootElement.GetProperty("commandId").GetGuid());
            Assert.Equal(BeforeHead, stateJson.RootElement.GetProperty("cloneHeadBefore").GetString());
            Assert.Equal(["ViegardSatelliteMDaemonAutoUpgrade"], launcher.ScheduledTaskCalls);
            Assert.Empty(launcher.DetachedLaunches);

            var command = Assert.Single(await store.ListRecentAsync("sat-a"));
            Assert.Equal(HostUpgradeCommandStatus.Running, command.Status);
        }
        finally
        {
            testPaths.Delete();
        }
    }

    [Fact]
    public async Task Worker_uses_configured_state_directory_for_state_and_transcript()
    {
        var testPaths = CreateTestPaths();
        try
        {
            var time = new FakeTimeProvider(Now);
            var store = new InMemoryHostUpgradeCommandStore(time);
            var requested = await store.RequestAsync("sat-a", "hannah");
            var launcher = new RecordingLauncher(testPaths.AppDirectory);
            launcher.CloneHeads.Enqueue(BeforeHead);
            launcher.CloneHeads.Enqueue(AfterHead);
            launcher.ScheduledResults.Enqueue(new HostUpgradeAgentProcessResult(1, "missing task"));
            launcher.DetachedResults.Enqueue(new HostUpgradeAgentProcessResult(0, "detached"));
            var worker = CreateWorker(store, launcher, testPaths.ScriptPath, testPaths.StateDirectory, time);

            await worker.PollOnceAsync();

            var launch = Assert.Single(launcher.DetachedLaunches);
            Assert.Equal(Path.Combine(testPaths.StateDirectory, TranscriptFileName), launch.TranscriptPath);
            Assert.True(File.Exists(Path.Combine(testPaths.StateDirectory, StateFileName)));
            Assert.False(File.Exists(Path.Combine(testPaths.AppDirectory, StateFileName)));

            await File.WriteAllTextAsync(Path.Combine(testPaths.AppDirectory, ".deployed-commit"), AfterHead);
            await File.WriteAllTextAsync(Path.Combine(testPaths.StateDirectory, TranscriptFileName), "completed");

            await worker.RunStartupAsync();

            var command = Assert.Single(await store.ListRecentAsync("sat-a"));
            Assert.Equal(requested.Id, command.Id);
            Assert.Equal(HostUpgradeCommandStatus.Succeeded, command.Status);
            Assert.False(File.Exists(Path.Combine(testPaths.StateDirectory, StateFileName)));
            Assert.False(File.Exists(Path.Combine(testPaths.StateDirectory, TranscriptFileName)));
        }
        finally
        {
            testPaths.Delete();
        }
    }

    [Fact]
    public async Task Launch_failure_completes_claimed_command_as_failed()
    {
        var testPaths = CreateTestPaths();
        try
        {
            var time = new FakeTimeProvider(Now);
            var store = new InMemoryHostUpgradeCommandStore(time);
            var requested = await store.RequestAsync("sat-a", "hannah");
            var launcher = new RecordingLauncher(testPaths.AppDirectory);
            launcher.CloneHeads.Enqueue(BeforeHead);
            launcher.ScheduledResults.Enqueue(new HostUpgradeAgentProcessResult(1, "missing task \u001b[31m"));
            launcher.DetachedResults.Enqueue(new HostUpgradeAgentProcessResult(1, "fallback failed"));
            var worker = CreateWorker(store, launcher, testPaths.ScriptPath, testPaths.StateDirectory, time);

            await worker.PollOnceAsync();

            var command = Assert.Single(await store.ListRecentAsync("sat-a"));
            Assert.Equal(requested.Id, command.Id);
            Assert.Equal(HostUpgradeCommandStatus.Failed, command.Status);
            Assert.NotNull(command.Detail);
            Assert.Contains("Could not launch", command.Detail, StringComparison.Ordinal);
            Assert.Contains("fallback failed", command.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain("\u001b", command.Detail, StringComparison.Ordinal);
            var launch = Assert.Single(launcher.DetachedLaunches);
            Assert.Equal(Path.Combine(testPaths.StateDirectory, TranscriptFileName), launch.TranscriptPath);
            Assert.False(File.Exists(Path.Combine(testPaths.StateDirectory, StateFileName)));
        }
        finally
        {
            testPaths.Delete();
        }
    }

    [Fact]
    public async Task Startup_completion_reports_success_when_marker_matches_clone_head()
    {
        var testPaths = CreateTestPaths();
        try
        {
            var time = new FakeTimeProvider(Now);
            var store = new InMemoryHostUpgradeCommandStore(time);
            var requested = await store.RequestAsync("sat-a", "hannah");
            Assert.NotNull(await store.ClaimNextPendingAsync("sat-a"));
            WriteStateFile(testPaths.StateDirectory, requested.Id);
            await File.WriteAllTextAsync(Path.Combine(testPaths.AppDirectory, ".deployed-commit"), AfterHead);
            await File.WriteAllTextAsync(Path.Combine(testPaths.StateDirectory, TranscriptFileName), "line one\r\nline two\u0001\r\n");
            var launcher = new RecordingLauncher(testPaths.AppDirectory);
            launcher.CloneHeads.Enqueue(AfterHead);
            var worker = CreateWorker(store, launcher, testPaths.ScriptPath, testPaths.StateDirectory, time);

            await worker.RunStartupAsync();

            var command = Assert.Single(await store.ListRecentAsync("sat-a"));
            Assert.Equal(HostUpgradeCommandStatus.Succeeded, command.Status);
            Assert.NotNull(command.Detail);
            Assert.Contains("aaaaaaaaa..bbbbbbbbb", command.Detail, StringComparison.Ordinal);
            Assert.Contains("Transcript tail", command.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain("\u0001", command.Detail, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(testPaths.StateDirectory, StateFileName)));
            Assert.False(File.Exists(Path.Combine(testPaths.StateDirectory, TranscriptFileName)));
        }
        finally
        {
            testPaths.Delete();
        }
    }

    [Fact]
    public async Task Startup_completion_reports_failure_when_marker_differs_from_clone_head()
    {
        var testPaths = CreateTestPaths();
        try
        {
            var time = new FakeTimeProvider(Now);
            var store = new InMemoryHostUpgradeCommandStore(time);
            var requested = await store.RequestAsync("sat-a", "hannah");
            Assert.NotNull(await store.ClaimNextPendingAsync("sat-a"));
            WriteStateFile(testPaths.StateDirectory, requested.Id);
            await File.WriteAllTextAsync(Path.Combine(testPaths.AppDirectory, ".deployed-commit"), "cccccccccccccccccccccccccccccccccccccccc");
            var launcher = new RecordingLauncher(testPaths.AppDirectory);
            launcher.CloneHeads.Enqueue(AfterHead);
            var worker = CreateWorker(store, launcher, testPaths.ScriptPath, testPaths.StateDirectory, time);

            await worker.RunStartupAsync();

            var command = Assert.Single(await store.ListRecentAsync("sat-a"));
            Assert.Equal(HostUpgradeCommandStatus.Failed, command.Status);
            Assert.NotNull(command.Detail);
            Assert.Contains("completion check failed", command.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ccccccccc", command.Detail, StringComparison.Ordinal);
            Assert.Contains("bbbbbbbbb", command.Detail, StringComparison.Ordinal);
        }
        finally
        {
            testPaths.Delete();
        }
    }

    [Fact]
    public async Task Startup_orphan_reconciliation_is_scoped_to_configured_target()
    {
        var testPaths = CreateTestPaths();
        try
        {
            var time = new FakeTimeProvider(Now);
            var store = new InMemoryHostUpgradeCommandStore(time);
            var own = await store.RequestAsync("sat-a", "hannah");
            var other = await store.RequestAsync("sat-b", "hannah");
            Assert.NotNull(await store.ClaimNextPendingAsync("sat-a"));
            Assert.NotNull(await store.ClaimNextPendingAsync("sat-b"));
            var launcher = new RecordingLauncher(testPaths.AppDirectory);
            var worker = CreateWorker(store, launcher, testPaths.ScriptPath, testPaths.StateDirectory, time);

            await worker.RunStartupAsync();

            var ownCommand = Assert.Single(await store.ListRecentAsync("sat-a"));
            var otherCommand = Assert.Single(await store.ListRecentAsync("sat-b"));
            Assert.Equal(own.Id, ownCommand.Id);
            Assert.Equal(other.Id, otherCommand.Id);
            Assert.Equal(HostUpgradeCommandStatus.Failed, ownCommand.Status);
            Assert.Equal(HostUpgradeCommandStatus.Running, otherCommand.Status);
        }
        finally
        {
            testPaths.Delete();
        }
    }

    [Fact]
    public async Task Non_windows_host_idles_without_claiming_commands()
    {
        var testPaths = CreateTestPaths();
        try
        {
            var time = new FakeTimeProvider(Now);
            var store = new InMemoryHostUpgradeCommandStore(time);
            await store.RequestAsync("sat-a", "hannah");
            var launcher = new RecordingLauncher(testPaths.AppDirectory) { IsWindows = false };
            var worker = CreateWorker(store, launcher, testPaths.ScriptPath, testPaths.StateDirectory, time);

            await worker.RunStartupAsync();
            await worker.PollOnceAsync();

            var command = Assert.Single(await store.ListRecentAsync("sat-a"));
            Assert.Equal(HostUpgradeCommandStatus.Pending, command.Status);
            Assert.Empty(launcher.ScheduledTaskCalls);
            Assert.Empty(launcher.DetachedLaunches);
        }
        finally
        {
            testPaths.Delete();
        }
    }

    private static HostUpgradeAgentWorker CreateWorker(
        InMemoryHostUpgradeCommandStore store,
        RecordingLauncher launcher,
        string scriptPath,
        string stateDirectory,
        FakeTimeProvider timeProvider,
        string target = "sat-a") =>
        new(
            Options.Create(new HostUpgradeAgentOptions
            {
                Target = target,
                SatelliteScriptPath = scriptPath,
                ClientName = "MDaemon",
                ScheduledTaskName = "ViegardSatelliteMDaemonAutoUpgrade",
                StateDirectory = stateDirectory,
                PollInterval = TimeSpan.FromSeconds(5),
                StuckStateGracePeriod = TimeSpan.FromMinutes(10),
            }),
            store,
            launcher,
            timeProvider,
            NullLogger<HostUpgradeAgentWorker>.Instance);

    private static void WriteStateFile(
        string stateDirectory,
        Guid commandId,
        DateTimeOffset? claimedAt = null,
        string? cloneHeadBefore = BeforeHead)
    {
        Directory.CreateDirectory(stateDirectory);
        var state = new
        {
            commandId,
            claimedAt = claimedAt ?? Now,
            cloneHeadBefore,
        };
        var raw = JsonSerializer.Serialize(state, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        File.WriteAllText(Path.Combine(stateDirectory, StateFileName), raw);
    }

    private static TestPaths CreateTestPaths()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "host-upgrade-agent-tests", Guid.NewGuid().ToString("N"));
        var appDirectory = Path.Combine(root, "app");
        var stateDirectory = Path.Combine(root, "state");
        var cloneRoot = Path.Combine(root, "clone");
        var scriptDirectory = Path.Combine(cloneRoot, "deploy", "windows");
        Directory.CreateDirectory(appDirectory);
        Directory.CreateDirectory(Path.Combine(cloneRoot, ".git"));
        Directory.CreateDirectory(scriptDirectory);
        var scriptPath = Path.Combine(scriptDirectory, "viegard-satellite.ps1");
        File.WriteAllText(scriptPath, "# test");
        return new TestPaths(root, appDirectory, stateDirectory, scriptPath);
    }

    private sealed class RecordingLauncher(string appBaseDirectory) : IHostUpgradeAgentLauncher
    {
        public bool IsWindows { get; set; } = true;

        public string AppBaseDirectory { get; } = appBaseDirectory;

        public Queue<string?> CloneHeads { get; } = [];

        public Queue<HostUpgradeAgentProcessResult> ScheduledResults { get; } = [];

        public Queue<HostUpgradeAgentProcessResult> DetachedResults { get; } = [];

        public List<string> CloneRootCalls { get; } = [];

        public List<string> ScheduledTaskCalls { get; } = [];

        public List<DetachedLaunch> DetachedLaunches { get; } = [];

        public ValueTask<string?> GetCloneHeadAsync(string cloneRoot, CancellationToken cancellationToken)
        {
            CloneRootCalls.Add(cloneRoot);
            return ValueTask.FromResult(CloneHeads.Count == 0 ? AfterHead : CloneHeads.Dequeue());
        }

        public ValueTask<HostUpgradeAgentProcessResult> RunScheduledTaskAsync(
            string scheduledTaskName,
            CancellationToken cancellationToken)
        {
            ScheduledTaskCalls.Add(scheduledTaskName);
            return ValueTask.FromResult(ScheduledResults.Count == 0
                ? new HostUpgradeAgentProcessResult(0, "task started")
                : ScheduledResults.Dequeue());
        }

        public ValueTask<HostUpgradeAgentProcessResult> LaunchDetachedUpgradeAsync(
            string satelliteScriptPath,
            string clientName,
            string transcriptPath,
            CancellationToken cancellationToken)
        {
            DetachedLaunches.Add(new DetachedLaunch(satelliteScriptPath, clientName, transcriptPath));
            return ValueTask.FromResult(DetachedResults.Count == 0
                ? new HostUpgradeAgentProcessResult(0, "detached")
                : DetachedResults.Dequeue());
        }
    }

    private sealed record TestPaths(string Root, string AppDirectory, string StateDirectory, string ScriptPath)
    {
        public void Delete()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed record DetachedLaunch(string SatelliteScriptPath, string ClientName, string TranscriptPath);
}
