using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Viegard.Application.Sources;
using Viegard.Application.Stores;

namespace Viegard.Sources.MDaemonLogs.Tests;

public sealed class MDaemonLogSourceTests
{
    [Fact]
    public async Task Offset_key_and_payload_reference_include_instance_key()
    {
        var paths = CreateTestPaths();
        try
        {
            var store = new RecordingOffsetStore();
            var source = new MDaemonLogSource(
                Options(paths.LogDirectory, instanceKey: "sat-a", ingestExisting: true),
                store,
                NullLogger<MDaemonLogSource>.Instance);

            var item = await ReadFirstAsync(source);

            Assert.Equal("mdaemon:logs", item.Observation.SourceId);
            Assert.Equal($"mdaemon/sat-a/{paths.FileName}/{paths.FileLength}/0", item.Observation.PayloadReference);
            Assert.Equal(
                paths.FileLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
                await store.GetAsync("mdaemon:logs", $"offset:sat-a:{paths.FileName}"));
        }
        finally
        {
            paths.Delete();
        }
    }

    [Fact]
    public async Task Different_instance_keys_do_not_share_offsets()
    {
        var paths = CreateTestPaths();
        try
        {
            var store = new RecordingOffsetStore();
            var sourceA = new MDaemonLogSource(
                Options(paths.LogDirectory, instanceKey: "sat-a", ingestExisting: true),
                store,
                NullLogger<MDaemonLogSource>.Instance);
            var sourceB = new MDaemonLogSource(
                Options(paths.LogDirectory, instanceKey: "sat-b", ingestExisting: true),
                store,
                NullLogger<MDaemonLogSource>.Instance);

            var itemA = await ReadFirstAsync(sourceA);
            var itemB = await ReadFirstAsync(sourceB);

            Assert.Equal($"mdaemon/sat-a/{paths.FileName}/{paths.FileLength}/0", itemA.Observation.PayloadReference);
            Assert.Equal($"mdaemon/sat-b/{paths.FileName}/{paths.FileLength}/0", itemB.Observation.PayloadReference);
            Assert.Equal(
                paths.FileLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
                await store.GetAsync("mdaemon:logs", $"offset:sat-a:{paths.FileName}"));
            Assert.Equal(
                paths.FileLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
                await store.GetAsync("mdaemon:logs", $"offset:sat-b:{paths.FileName}"));
        }
        finally
        {
            paths.Delete();
        }
    }

    private static MDaemonSourceOptions Options(
        string logDirectory,
        string instanceKey,
        bool ingestExisting) =>
        new()
        {
            Enabled = true,
            LogDirectory = logDirectory,
            InstanceKey = instanceKey,
            IngestExistingOnFirstRun = ingestExisting,
            PollInterval = TimeSpan.FromHours(1),
            Files =
            {
                new MDaemonLogFileOptions
                {
                    Pattern = "MDaemon-*-SMTP-(in).log",
                    LogKind = "SmtpIn",
                },
            },
        };

    private static async Task<ObservedItem> ReadFirstAsync(MDaemonLogSource source)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var enumerator = source.ObserveAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        return enumerator.Current;
    }

    private static TestPaths CreateTestPaths()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "mdaemon-log-source-tests", Guid.NewGuid().ToString("N"));
        var logDirectory = Path.Combine(root, "Logs");
        Directory.CreateDirectory(logDirectory);
        var fileName = "MDaemon-2026-09-17-SMTP-(in).log";
        var filePath = Path.Combine(logDirectory, fileName);
        var content = "SMTP inbound line\r\n";
        File.WriteAllText(filePath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return new TestPaths(
            root,
            logDirectory,
            fileName,
            Encoding.UTF8.GetByteCount(content));
    }

    private sealed class RecordingOffsetStore : ISourceOffsetStore
    {
        private readonly ConcurrentDictionary<(string SourceId, string Key), string> _offsets = new();

        public ValueTask<string?> GetAsync(string sourceId, string key, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_offsets.TryGetValue((sourceId, key), out var value) ? value : null);

        public ValueTask SetAsync(string sourceId, string key, string value, CancellationToken cancellationToken = default)
        {
            _offsets[(sourceId, key)] = value;
            return ValueTask.CompletedTask;
        }
    }

    private sealed record TestPaths(string Root, string LogDirectory, string FileName, int FileLength)
    {
        public void Delete()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
