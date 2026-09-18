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
            EvidenceWeight = CustomSignature.MaxEvidenceWeight + 0.01,
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
    public void Validation_enforces_additional_pattern_count_and_per_term_rules()
    {
        var valid = Signature(additionalPatterns:
        [
            new string('a', CustomSignature.MaxPatternLength),
            "campaign",
        ]);
        Assert.True(CustomSignatureValidator.Validate(valid).IsValid);

        var invalid = valid with
        {
            AdditionalPatterns =
            [
                "",
                new string('b', CustomSignature.MaxPatternLength + 1),
                "extra",
            ],
        };

        var result = CustomSignatureValidator.Validate(invalid);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("at most 3 required patterns", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("Additional required pattern 2 is required", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("Additional required pattern 3 must be", StringComparison.Ordinal));
    }

    [Fact]
    public void Normalize_forces_contains_all_when_additional_terms_are_present()
    {
        var normalized = CustomSignatureValidator.Normalize(Signature(
            matchType: CustomSignatureMatchType.Prefix,
            additionalPatterns: [" second "]));

        Assert.Equal(CustomSignatureMatchType.ContainsAll, normalized.MatchType);
        Assert.Equal("second", Assert.Single(normalized.AdditionalPatterns));
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
    public async Task In_memory_store_round_trips_additional_patterns()
    {
        var store = new InMemoryCustomSignatureStore();
        var signature = Signature(
            matchType: CustomSignatureMatchType.ContainsAll,
            additionalPatterns: ["ref=", "%2Fpage%2F"]);

        var saved = await store.UpsertAsync(signature);
        var restored = await store.GetAsync(saved.Id);

        Assert.NotNull(restored);
        Assert.Equal(CustomSignatureMatchType.ContainsAll, restored.MatchType);
        Assert.Equal(["ref=", "%2Fpage%2F"], restored.AdditionalPatterns);
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
    public async Task Rule_source_preserves_high_evidence_weights()
    {
        // Regression net for the live find: the evidence factory clamped
        // every score to 1.0, so weight-4 and weight-5 signatures could
        // never reach the review or automatic-action bands from a single
        // matched event despite the 5.0 weight cap.
        var store = new FakeSignatureStore(
        [
            Signature(name: "armed", target: CustomSignatureTarget.HttpQuery, pattern: "ref=aftership", evidenceWeight: 4.0),
        ]);
        var source = new CustomSignatureRuleSource(store, new DetectionOptions(), new RecordingDiagnostics());
        await source.RefreshAsync();

        var evidence = source.Evaluate(HttpEvent("/track?ref=aftership"));

        Assert.Equal(4.0, Assert.Single(evidence).Score);
    }

    [Fact]
    public async Task Rule_source_preserves_the_maximum_evidence_weight()
    {
        var store = new FakeSignatureStore(
        [
            Signature(name: "maxed", target: CustomSignatureTarget.HttpQuery, pattern: "ref=aftership", evidenceWeight: CustomSignature.MaxEvidenceWeight),
        ]);
        var source = new CustomSignatureRuleSource(store, new DetectionOptions(), new RecordingDiagnostics());
        await source.RefreshAsync();

        var evidence = source.Evaluate(HttpEvent("/track?ref=aftership"));

        Assert.Equal(CustomSignature.MaxEvidenceWeight, Assert.Single(evidence).Score);
    }

    [Fact]
    public void Matcher_contains_all_requires_every_literal_term_case_insensitively()
    {
        var signature = Signature(
            target: CustomSignatureTarget.HttpQuery,
            matchType: CustomSignatureMatchType.ContainsAll,
            pattern: "ref=",
            additionalPatterns: ["%2Fpage%2F", "campaign=abc"]);
        var options = new DetectionOptions();

        Assert.True(CustomSignatureMatcher.Matches(
            HttpEvent("/track?REF=%2fPAGE%2f&Campaign=ABC"),
            signature,
            options));
        Assert.False(CustomSignatureMatcher.Matches(
            HttpEvent("/track?ref=%2Fpage%2F"),
            signature,
            options));
    }

    [Fact]
    public void Matcher_contains_all_respects_scan_cap()
    {
        var signature = Signature(
            matchType: CustomSignatureMatchType.ContainsAll,
            pattern: "/track",
            additionalPatterns: ["campaign=aftership"]);
        var options = new DetectionOptions { MaxInputCharsToScan = "/track?x=1".Length };

        var matched = CustomSignatureMatcher.Matches(
            HttpEvent("/track?x=1&campaign=aftership"),
            signature,
            options);

        Assert.False(matched);
    }

    [Fact]
    public void Matcher_contains_all_with_no_additional_terms_matches_like_contains()
    {
        var signature = Signature(
            target: CustomSignatureTarget.HttpQuery,
            matchType: CustomSignatureMatchType.ContainsAll,
            pattern: "token=abc");

        Assert.True(CustomSignatureMatcher.Matches(
            HttpEvent("/callback?TOKEN=ABC"),
            signature,
            new DetectionOptions()));
    }

    [Fact]
    public void Event_kind_target_matches_mdaemon_kinds_by_namespaced_name()
    {
        var signature = Signature(
            target: CustomSignatureTarget.EventKind,
            matchType: CustomSignatureMatchType.Contains,
            pattern: "mdaemon/ScreeningBlocked");
        var options = new DetectionOptions();

        Assert.True(CustomSignatureMatcher.Matches(
            MDaemonEvent(MDaemonEventKind.ScreeningBlocked),
            signature,
            options));
        Assert.False(CustomSignatureMatcher.Matches(
            MDaemonEvent(MDaemonEventKind.AuthenticationFailed),
            signature,
            options));
        Assert.False(CustomSignatureMatcher.Matches(
            HttpEvent("/anything?screeningblocked=1"),
            signature,
            options));
    }

    [Fact]
    public void Event_kind_target_prefix_selects_a_whole_source_family()
    {
        var signature = Signature(
            target: CustomSignatureTarget.EventKind,
            matchType: CustomSignatureMatchType.Prefix,
            pattern: "mdaemon/");
        var options = new DetectionOptions();

        Assert.True(CustomSignatureMatcher.Matches(MDaemonEvent(MDaemonEventKind.ScreeningBlocked), signature, options));
        Assert.True(CustomSignatureMatcher.Matches(MDaemonEvent(MDaemonEventKind.IpBlocked), signature, options));
        Assert.False(CustomSignatureMatcher.Matches(HttpEvent("/mdaemon/looks-like-it"), signature, options));
    }

    [Fact]
    public void Event_kind_target_matches_http_events_generically()
    {
        var signature = Signature(
            target: CustomSignatureTarget.EventKind,
            matchType: CustomSignatureMatchType.Contains,
            pattern: "http/request");

        Assert.True(CustomSignatureMatcher.Matches(HttpEvent("/any"), signature, new DetectionOptions()));
    }

    [Fact]
    public void High_weight_event_kind_signature_is_valid_up_to_the_cap()
    {
        var signature = Signature(target: CustomSignatureTarget.EventKind, pattern: "mdaemon/ScreeningBlocked")
            with
        { EvidenceWeight = CustomSignature.MaxEvidenceWeight };

        Assert.True(CustomSignatureValidator.Validate(signature).IsValid);
    }

    [Fact]
    public async Task Rule_source_and_shared_matcher_return_same_verdict()
    {
        var signature = Signature(
            target: CustomSignatureTarget.HttpQuery,
            matchType: CustomSignatureMatchType.ContainsAll,
            pattern: "ref=",
            additionalPatterns: ["%2Fpage%2F"]);
        var options = new DetectionOptions();
        var source = new CustomSignatureRuleSource(
            new FakeSignatureStore([signature]),
            options,
            new RecordingDiagnostics());
        await source.RefreshAsync();
        var matchingEvent = HttpEvent("/track?ref=%2Fpage%2F");
        var nonMatchingEvent = HttpEvent("/track?ref=aftership");

        Assert.Equal(
            CustomSignatureMatcher.Matches(matchingEvent, signature, options),
            source.Evaluate(matchingEvent).Count > 0);
        Assert.Equal(
            CustomSignatureMatcher.Matches(nonMatchingEvent, signature, options),
            source.Evaluate(nonMatchingEvent).Count > 0);
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
        IReadOnlyList<string>? additionalPatterns = null,
        string category = "test",
        int severity = 3,
        double evidenceWeight = 1.0) => new()
    {
        Id = ViegardId.New(),
        Name = name,
        Enabled = true,
        Target = target,
        MatchType = matchType,
        Pattern = pattern,
        AdditionalPatterns = additionalPatterns ?? [],
        Category = category,
        Severity = severity,
        EvidenceWeight = evidenceWeight,
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

    private static NormalizedEvent MDaemonEvent(MDaemonEventKind kind) => new()
    {
        Id = ViegardId.New(),
        SourceId = "mdaemon-test",
        SourceType = "mdaemon",
        OccurredAt = DateTimeOffset.UtcNow,
        Entities = [new EntityRef(EntityKind.IpAddress, "203.0.113.10")],
        Payload = new MDaemonLogEvent
        {
            LogKind = MDaemonLogKind.Screening,
            EventKind = kind,
            RemoteIp = "203.0.113.10",
            Message = "test line",
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

        public ValueTask<KeysetPage<CustomSignature>> ListPageAsync(
            Guid? beforeId,
            int pageSize,
            SignatureListFilter? filter = null,
            ListSort<SignatureSortColumn>? sort = null,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnList)
            {
                throw new InvalidOperationException("configured failure");
            }

            var safePageSize = Math.Clamp(pageSize, 1, 200);
            var ordered = CustomSignatureFilter.Apply(Signatures, filter?.Text).ToList();
            ordered.Sort(SignatureComparison(sort) ?? CompareBy<CustomSignature, Guid>(s => s.Id, s => s.Id, SortDirection.Desc));
            var totalCount = ordered.Count;
            var startIndex = 0;
            if (beforeId is not null)
            {
                var cursorIndex = ordered.FindIndex(s => s.Id == beforeId.Value);
                startIndex = cursorIndex < 0 ? ordered.Count : cursorIndex + 1;
            }

            var pagePlusOne = ordered.Skip(startIndex).Take(safePageSize + 1).ToList();
            var items = pagePlusOne.Take(safePageSize).ToList();
            var nextCursor = pagePlusOne.Count > safePageSize && items.Count > 0 ? items[^1].Id : (Guid?)null;
            var preceding = items.Count == 0 ? 0 : ordered.FindIndex(s => s.Id == items[0].Id);
            return ValueTask.FromResult(new KeysetPage<CustomSignature>(items, nextCursor, totalCount, preceding));
        }

        public ValueTask<Guid?> GetPageCursorAsync(
            int pageNumber,
            int pageSize,
            SignatureListFilter? filter = null,
            ListSort<SignatureSortColumn>? sort = null,
            CancellationToken cancellationToken = default)
        {
            var safePageSize = Math.Clamp(pageSize, 1, 200);
            var ordered = CustomSignatureFilter.Apply(Signatures, filter?.Text).ToList();
            ordered.Sort(SignatureComparison(sort) ?? CompareBy<CustomSignature, Guid>(s => s.Id, s => s.Id, SortDirection.Desc));
            var totalPages = Math.Max(1, (long)Math.Ceiling(ordered.Count / (double)safePageSize));
            var safePageNumber = Math.Clamp((long)pageNumber, 1, totalPages);
            if (safePageNumber <= 1)
            {
                return ValueTask.FromResult<Guid?>(null);
            }

            var boundaryIndex = (safePageNumber - 1) * safePageSize - 1;
            return ValueTask.FromResult(boundaryIndex >= 0 && boundaryIndex < ordered.Count
                ? ordered[(int)boundaryIndex].Id
                : (Guid?)null);
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

        private static Comparison<CustomSignature>? SignatureComparison(ListSort<SignatureSortColumn>? sort)
        {
            if (sort is not { } active || !Enum.IsDefined(typeof(SortDirection), active.Direction))
            {
                return null;
            }

            return active.Column switch
            {
                SignatureSortColumn.Name => CompareBy<CustomSignature, string>(s => s.Name, s => s.Id, active.Direction, StringComparer.Ordinal),
                SignatureSortColumn.Target => CompareBy<CustomSignature, int>(s => (int)s.Target, s => s.Id, active.Direction),
                SignatureSortColumn.Match => CompareBy<CustomSignature, int>(s => (int)s.MatchType, s => s.Id, active.Direction),
                SignatureSortColumn.Category => CompareBy<CustomSignature, string>(s => s.Category, s => s.Id, active.Direction, StringComparer.Ordinal),
                SignatureSortColumn.Severity => CompareBy<CustomSignature, int>(s => s.Severity, s => s.Id, active.Direction),
                SignatureSortColumn.Enabled => CompareBy<CustomSignature, bool>(s => s.Enabled, s => s.Id, active.Direction),
                SignatureSortColumn.Updated => CompareBy<CustomSignature, DateTimeOffset>(s => s.UpdatedAt, s => s.Id, active.Direction),
                SignatureSortColumn.Version => CompareBy<CustomSignature, int>(s => s.Version, s => s.Id, active.Direction),
                _ => null,
            };
        }

        private static Comparison<T> CompareBy<T, TKey>(
            Func<T, TKey> getKey,
            Func<T, Guid> getId,
            SortDirection direction,
            IComparer<TKey>? comparer = null)
        {
            var keyComparer = comparer ?? Comparer<TKey>.Default;
            return (left, right) =>
            {
                var result = keyComparer.Compare(getKey(left), getKey(right));
                if (result == 0)
                {
                    result = getId(left).CompareTo(getId(right));
                }

                return direction == SortDirection.Asc ? result : -result;
            };
        }
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
