using Viegard.Application.Configuration;
using Viegard.Application.Detection;
using Viegard.Application.Stores;
using Viegard.Domain;
using Viegard.Domain.Configuration;
using Viegard.Domain.Events;
using Viegard.Domain.Incidents;
using Viegard.Persistence.InMemory;

namespace Viegard.Application.Tests;

public sealed class CustomSignatureTests
{
    [Fact]
    public void Validation_enforces_required_fields_and_caps()
    {
        var valid = Signature(pattern: new string('a', CustomSignature.MaxPatternLength));
        Assert.True(CustomSignatureValidator.Validate(valid).IsValid);

        var invalid = valid with
        {
            Name = "",
            Pattern = new string('b', CustomSignature.MaxPatternLength + 1),
            Category = "",
            Severity = 11,
            EvidenceWeight = 1.01,
            Target = (CustomSignatureTarget)(-1),
            MatchType = (CustomSignatureMatchType)(-1),
        };

        var result = CustomSignatureValidator.Validate(invalid);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Name", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("Pattern", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("Severity", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("Evidence weight", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("Target", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("Match type", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Seed_signature_is_inserted_once_by_in_memory_store()
    {
        var store = new InMemoryCustomSignatureStore();

        var first = await store.ListAsync();
        var second = await store.ListAsync();

        var seed = Assert.Single(first);
        Assert.Equal(CustomSignatureSeeds.AftershipReferralBotName, seed.Name);
        Assert.Equal(seed.Id, Assert.Single(second).Id);
    }

    [Fact]
    public async Task In_memory_incident_store_uses_keyset_cursor()
    {
        var store = new InMemoryIncidentStore();
        var first = Incident();
        await Task.Delay(2);
        var second = Incident();
        await Task.Delay(2);
        var third = Incident();
        await store.UpsertAsync(first);
        await store.UpsertAsync(second);
        await store.UpsertAsync(third);

        var page1 = await store.ListPageAsync(beforeId: null, pageSize: 2);
        var page2 = await store.ListPageAsync(page1.NextCursor, pageSize: 2);

        Assert.Equal([third.Id, second.Id], page1.Items.Select(i => i.Id));
        Assert.Equal(3, page1.TotalCount);
        Assert.Equal(0, page1.Preceding);
        Assert.Equal(first.Id, Assert.Single(page2.Items).Id);
        Assert.Equal(3, page2.TotalCount);
        Assert.Equal(2, page2.Preceding);
        Assert.Null(page2.NextCursor);
    }

    [Fact]
    public async Task Rule_source_matches_contains_prefix_case_and_target()
    {
        var store = new FakeSignatureStore(
        [
            Signature(name: "ua", target: CustomSignatureTarget.HttpUserAgent, matchType: CustomSignatureMatchType.Contains, pattern: "BOT"),
            Signature(name: "path", target: CustomSignatureTarget.HttpPath, matchType: CustomSignatureMatchType.Prefix, pattern: "/Admin"),
            Signature(name: "query", target: CustomSignatureTarget.HttpQuery, matchType: CustomSignatureMatchType.Contains, pattern: "token=abc"),
        ]);
        var source = new CustomSignatureRuleSource(store, new DetectionOptions(), new RecordingDiagnostics());
        await source.RefreshAsync();

        var evidence = source.Evaluate(HttpEvent("/admin/login?token=ABC", userAgent: "friendly bot"));

        Assert.Equal(3, evidence.Count);
        Assert.All(evidence, e => Assert.Equal(1.0, e.Score));
    }

    [Fact]
    public async Task Seeded_aftership_pattern_matches_realistic_http_query()
    {
        var store = new InMemoryCustomSignatureStore();
        var source = new CustomSignatureRuleSource(store, new DetectionOptions(), new RecordingDiagnostics());
        await source.RefreshAsync();

        var evidence = source.Evaluate(HttpEvent("/track/order?id=42&ref=aftership&utm_source=mail"));

        var item = Assert.Single(evidence);
        Assert.Contains("aftership-referral-bot", item.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1.0, item.Score);
    }

    [Fact]
    public async Task Invalid_signature_is_skipped_without_clearing_valid_rules()
    {
        var diagnostics = new RecordingDiagnostics();
        var valid = Signature(pattern: "first");
        var invalid = Signature(pattern: "", severity: 12);
        var store = new FakeSignatureStore([valid, invalid]);
        var source = new CustomSignatureRuleSource(store, new DetectionOptions(), diagnostics);

        await source.RefreshAsync();

        Assert.Equal(1, source.RuleCount);
        Assert.Single(diagnostics.InvalidSignatures);
        Assert.Single(source.Evaluate(HttpEvent("/first")));
        Assert.Empty(source.Evaluate(HttpEvent("/second")));
    }

    [Fact]
    public async Task Refresh_failure_keeps_last_known_good_rules()
    {
        var store = new FakeSignatureStore([Signature(pattern: "first")]);
        var diagnostics = new RecordingDiagnostics();
        var source = new CustomSignatureRuleSource(store, new DetectionOptions(), diagnostics);
        await source.RefreshAsync();

        store.ThrowOnList = true;
        await source.RefreshAsync();

        Assert.Single(diagnostics.RefreshFailures);
        Assert.Single(source.Evaluate(HttpEvent("/first")));
    }

    [Fact]
    public async Task Refresh_atomically_swaps_rule_set()
    {
        var store = new FakeSignatureStore([Signature(pattern: "first")]);
        var source = new CustomSignatureRuleSource(store, new DetectionOptions(), new RecordingDiagnostics());
        await source.RefreshAsync();

        store.Signatures = [Signature(pattern: "second")];
        await source.RefreshAsync();

        Assert.Empty(source.Evaluate(HttpEvent("/first")));
        Assert.Single(source.Evaluate(HttpEvent("/second")));
    }

    private static CustomSignature Signature(
        string name = "sig",
        CustomSignatureTarget target = CustomSignatureTarget.HttpUri,
        CustomSignatureMatchType matchType = CustomSignatureMatchType.Contains,
        string pattern = "needle",
        string category = "test",
        int severity = 3) => new()
    {
        Id = ViegardId.New(),
        Name = name,
        Enabled = true,
        Target = target,
        MatchType = matchType,
        Pattern = pattern,
        Category = category,
        Severity = severity,
        EvidenceWeight = 1.0,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        UpdatedBy = "test",
        Version = 1,
    };

    private static Incident Incident() => new()
    {
        Id = ViegardId.New(),
        CorrelationKey = $"ip=198.51.100.{Random.Shared.Next(1, 200)}",
        WindowStart = DateTimeOffset.UtcNow,
        WindowEnd = DateTimeOffset.UtcNow,
        EventIds = [],
        Evidence = [],
        State = IncidentState.Open,
    };

    private static NormalizedEvent HttpEvent(string uri, string userAgent = "Mozilla/5.0") => new()
    {
        Id = ViegardId.New(),
        SourceId = "nginx-test",
        SourceType = "syslog",
        OccurredAt = DateTimeOffset.UtcNow,
        Entities = [new EntityRef(EntityKind.IpAddress, "203.0.113.10")],
        Payload = new HttpRequestEvent
        {
            RemoteAddress = "203.0.113.10",
            Method = "GET",
            Uri = uri,
            Protocol = "HTTP/1.1",
            StatusCode = 200,
            UserAgent = userAgent,
            Host = "www.example.com",
        },
        RawObservationId = ViegardId.New(),
    };

    private sealed class FakeSignatureStore(IReadOnlyList<CustomSignature> signatures) : ICustomSignatureStore
    {
        private long _version;

        public IReadOnlyList<CustomSignature> Signatures { get; set; } = signatures;

        public bool ThrowOnList { get; set; }

        public long CurrentChangeVersion => Volatile.Read(ref _version);

        public ValueTask<IReadOnlyList<CustomSignature>> ListAsync(CancellationToken cancellationToken = default)
        {
            if (ThrowOnList)
            {
                throw new InvalidOperationException("configured failure");
            }

            return ValueTask.FromResult(Signatures);
        }

        public ValueTask<CustomSignature?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Signatures.FirstOrDefault(s => s.Id == id));

        public ValueTask<CustomSignature> UpsertAsync(CustomSignature signature, CancellationToken cancellationToken = default)
        {
            Signatures = Signatures.Where(s => s.Id != signature.Id).Append(signature).ToList();
            Interlocked.Increment(ref _version);
            return ValueTask.FromResult(signature);
        }

        public ValueTask<CustomSignature?> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var existing = Signatures.FirstOrDefault(s => s.Id == id);
            Signatures = Signatures.Where(s => s.Id != id).ToList();
            Interlocked.Increment(ref _version);
            return ValueTask.FromResult(existing);
        }

        public ValueTask<long> WaitForChangeAsync(
            long lastSeenVersion,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CurrentChangeVersion);
    }

    private sealed class RecordingDiagnostics : ICustomSignatureRuleDiagnostics
    {
        public List<CustomSignature> InvalidSignatures { get; } = [];

        public List<Exception> RefreshFailures { get; } = [];

        public void InvalidSignatureSkipped(CustomSignature signature, IReadOnlyList<string> errors) =>
            InvalidSignatures.Add(signature);

        public void RefreshFailed(Exception exception) => RefreshFailures.Add(exception);
    }
}
