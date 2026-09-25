using Viegard.Application.Doh;
using Viegard.Persistence.InMemory;

namespace Viegard.Application.Tests;

public sealed class DohProbeSummaryTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FromCounts_aggregates_each_status_and_total()
    {
        var counts = new[]
        {
            new DohProbeStatusCount(DohProbeStatus.Confirmed, null, 5),
            new DohProbeStatusCount(DohProbeStatus.RespondedNonCompliant, null, 2),
            new DohProbeStatusCount(DohProbeStatus.Refused, 403, 3),
            new DohProbeStatusCount(DohProbeStatus.Refused, null, 1),
            new DohProbeStatusCount(DohProbeStatus.Timeout, null, 4),
            new DohProbeStatusCount(DohProbeStatus.Unprobed, null, 6),
        };

        var summary = DohProbeOutcomeSummary.FromCounts(counts, At);

        Assert.Equal(At, summary.GeneratedAt);
        Assert.Equal(21, summary.Total);
        Assert.Equal(5, summary.Confirmed);
        Assert.Equal(2, summary.RespondedNonCompliant);
        Assert.Equal(4, summary.Refused);
        Assert.Equal(4, summary.Timeout);
        Assert.Equal(6, summary.Unprobed);
    }

    [Fact]
    public void FromCounts_splits_refused_by_http_status_ordered_by_count_desc()
    {
        var counts = new[]
        {
            new DohProbeStatusCount(DohProbeStatus.Refused, 403, 2),
            new DohProbeStatusCount(DohProbeStatus.Refused, null, 5),
            new DohProbeStatusCount(DohProbeStatus.Refused, 404, 5),
        };

        var summary = DohProbeOutcomeSummary.FromCounts(counts, At);

        Assert.Equal(12, summary.Refused);
        Assert.Collection(
            summary.RefusedByHttpStatus,
            first =>
            {
                Assert.Equal(404, first.HttpStatus);
                Assert.Equal(5, first.Count);
            },
            second =>
            {
                Assert.Null(second.HttpStatus);
                Assert.Equal(5, second.Count);
            },
            third =>
            {
                Assert.Equal(403, third.HttpStatus);
                Assert.Equal(2, third.Count);
            });
    }

    [Fact]
    public async Task InMemory_store_returns_null_before_any_save()
    {
        var store = new InMemoryDohProbeSummaryStore();

        Assert.Null(await store.GetAsync());
    }

    [Fact]
    public async Task InMemory_store_has_no_prior_after_first_save()
    {
        var store = new InMemoryDohProbeSummaryStore();
        var first = DohProbeOutcomeSummary.FromCounts(
            new[] { new DohProbeStatusCount(DohProbeStatus.Confirmed, null, 1) },
            At);

        await store.SaveCurrentAsync(first);
        var snapshot = await store.GetAsync();

        Assert.NotNull(snapshot);
        Assert.Equal(1, snapshot!.Current.Confirmed);
        Assert.Null(snapshot.Prior);
    }

    [Fact]
    public async Task InMemory_store_rolls_previous_current_into_prior_on_second_save()
    {
        var store = new InMemoryDohProbeSummaryStore();
        var first = DohProbeOutcomeSummary.FromCounts(
            new[] { new DohProbeStatusCount(DohProbeStatus.Confirmed, null, 1) },
            At);
        var second = DohProbeOutcomeSummary.FromCounts(
            new[] { new DohProbeStatusCount(DohProbeStatus.Confirmed, null, 4) },
            At.AddMinutes(5));

        await store.SaveCurrentAsync(first);
        await store.SaveCurrentAsync(second);
        var snapshot = await store.GetAsync();

        Assert.NotNull(snapshot);
        Assert.Equal(4, snapshot!.Current.Confirmed);
        Assert.NotNull(snapshot.Prior);
        Assert.Equal(1, snapshot.Prior!.Confirmed);
    }
}
