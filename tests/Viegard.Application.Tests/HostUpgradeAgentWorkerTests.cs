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
            PollInterval = TimeSpan.FromSeconds(4),
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
            var worker = CreateWorker(store, launcher, testPaths.ScriptPath, time);

            await worker.PollOnceAsync();

            var statePath = Path.Combine(testPaths.AppDirectory, StateFileName);
            Assert.True(File.Exists(statePath));
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
            var worker = CreateWorker(store, launcher, testPaths.ScriptPath, time);

            await worker.PollOnceAsync();

            var command = Assert.Single(await store.ListRecentAsync("sat-a"));
            Assert.Equal(requested.Id, command.Id);
            Assert.Equal(HostUpgradeCommandStatus.Failed, command.Status);
            Assert.NotNull(command.Detail);
            Assert.Contains("Could not launch", command.Detail, StringComparison.Ordinal);
            Assert.Contains("fallback failed", command.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain("\u001b", command.Detail, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(testPaths.AppDirectory, StateFileName)));
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
            WriteStateFile(testPaths.AppDirectory, requested.Id);
            await File.WriteAllTextAsync(Path.Combine(testPaths.AppDirectory, ".deployed-commit"), AfterHead);
            await File.WriteAllTextAsync(Path.Combine(testPaths.AppDirectory, TranscriptFileName), "line one\r\nline two\u0001\r\n");
            var launcher = new RecordingLauncher(testPaths.AppDirectory);
            launcher.CloneHeads.Enqueue(AfterHead);
            var worker = CreateWorker(store, launcher, testPaths.ScriptPath, time);

            await worker.RunStartupAsync();

            var command = Assert.Single(await store.ListRecentAsync("sat-a"));
            Assert.Equal(HostUpgradeCommandStatus.Succeeded, command.Status);
            Assert.NotNull(command.Detail);
            Assert.Contains("aaaaaaaaa..bbbbbbbbb", command.Detail, StringComparison.Ordinal);
            Assert.Contains("Transcript tail", command.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain("\u0001", command.Detail, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(testPaths.AppDirectory, StateFileName)));
            Assert.False(File.Exists(Path.Combine(testPaths.AppDirectory, TranscriptFileName)));
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
            WriteStateFile(testPaths.AppDirectory, requested.Id);
            await File.WriteAllTextAsync(Path.Combine(testPaths.AppDirectory, ".deployed-commit"), "cccccccccccccccccccccccccccccccccccccccc");
            var launcher = new RecordingLauncher(testPaths.AppDirectory);
            launcher.CloneHeads.Enqueue(AfterHead);
            var worker = CreateWorker(store, launcher, testPaths.ScriptPath, time);

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
            var worker = CreateWorker(store, launcher, testPaths.ScriptPath, time);

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
            var worker = CreateWorker(store, launcher, testPaths.ScriptPath, time);

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
        FakeTimeProvider timeProvider,
        string target = "sat-a") =>
        new(
            Options.Create(new HostUpgradeAgentOptions
            {
                Target = target,
                SatelliteScriptPath = scriptPath,
                ClientName = "MDaemon",
                ScheduledTaskName = "ViegardSatelliteMDaemonAutoUpgrade",
                PollInterval = TimeSpan.FromSeconds(5),
            }),
            store,
            launcher,
            timeProvider,
            NullLogger<HostUpgradeAgentWorker>.Instance);

    private static void WriteStateFile(string appDirectory, Guid commandId)
    {
        var state = new
        {
            commandId,
            claimedAt = Now,
            cloneHeadBefore = BeforeHead,
        };
        var raw = JsonSerializer.Serialize(state, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        File.WriteAllText(Path.Combine(appDirectory, StateFileName), raw);
    }

    private static TestPaths CreateTestPaths()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "host-upgrade-agent-tests", Guid.NewGuid().ToString("N"));
        var appDirectory = Path.Combine(root, "app");
        var cloneRoot = Path.Combine(root, "clone");
        var scriptDirectory = Path.Combine(cloneRoot, "deploy", "windows");
        Directory.CreateDirectory(appDirectory);
        Directory.CreateDirectory(Path.Combine(cloneRoot, ".git"));
        Directory.CreateDirectory(scriptDirectory);
        var scriptPath = Path.Combine(scriptDirectory, "viegard-satellite.ps1");
        File.WriteAllText(scriptPath, "# test");
        return new TestPaths(root, appDirectory, scriptPath);
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

    private sealed record TestPaths(string Root, string AppDirectory, string ScriptPath)
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
