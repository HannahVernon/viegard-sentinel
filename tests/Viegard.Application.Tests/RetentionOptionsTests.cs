using Microsoft.Extensions.Options;
using Viegard.Application.Retention;

namespace Viegard.Application.Tests;

public sealed class RetentionOptionsTests
{
    [Fact]
    public void Defaults_keep_every_purge_target_disabled()
    {
        var options = new RetentionOptions();
        var result = new RetentionOptionsValidator().Validate(Options.DefaultName, options);

        Assert.True(result.Succeeded);
        Assert.Empty(options.ConfiguredPeriods());
        Assert.Equal(RetentionOptions.DefaultBatchSize, options.EffectiveBatchSize);
    }

    [Fact]
    public void Validator_rejects_negative_values()
    {
        var result = new RetentionOptionsValidator().Validate(
            Options.DefaultName,
            new RetentionOptions
            {
                RawObservationsDays = -1,
                ExpiredAdminSessionsDays = -30,
                BatchSize = -5,
                StartupDelay = TimeSpan.FromSeconds(-1),
                CheckInterval = TimeSpan.Zero,
            });

        Assert.True(result.Failed);
        Assert.NotNull(result.Failures);
        Assert.Equal(5, result.Failures!.Count());
    }

    [Fact]
    public void Batch_size_is_clamped_to_sane_bounds()
    {
        Assert.Equal(RetentionOptions.MinBatchSize, new RetentionOptions { BatchSize = 0 }.EffectiveBatchSize);
        Assert.Equal(RetentionOptions.MaxBatchSize, new RetentionOptions { BatchSize = int.MaxValue }.EffectiveBatchSize);
        Assert.Equal(250, new RetentionOptions { BatchSize = 250 }.EffectiveBatchSize);
    }

    [Fact]
    public void Configured_periods_include_only_non_null_targets()
    {
        var periods = new RetentionOptions
        {
            EventsDays = 90,
            IncidentsDays = null,
            AuditRecordsDays = 365,
        }.ConfiguredPeriods();

        Assert.Equal(
            [RetentionTarget.Events, RetentionTarget.AuditRecords],
            periods.Select(p => p.Target));
    }
}
