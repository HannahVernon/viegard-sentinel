using Microsoft.Extensions.Options;
using Viegard.Application.Retention;

namespace Viegard.Application.Tests;

public sealed class RetentionOptionsTests
{
    [Fact]
    public void Defaults_enable_advisor_consult_purge_only()
    {
        var options = new RetentionOptions();
        var result = new RetentionOptionsValidator().Validate(Options.DefaultName, options);

        Assert.True(result.Succeeded);
        var period = Assert.Single(options.ConfiguredPeriods());
        Assert.Equal(RetentionTarget.LocalModelAdvisorConsults, period.Target);
        Assert.Equal(90, period.Days);
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
                LocalModelAdvisorConsultsDays = -90,
                BatchSize = -5,
                StartupDelay = TimeSpan.FromSeconds(-1),
                CheckInterval = TimeSpan.Zero,
            });

        Assert.True(result.Failed);
        Assert.NotNull(result.Failures);
        Assert.Equal(6, result.Failures!.Count());
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
            LocalModelAdvisorConsultsDays = null,
        }.ConfiguredPeriods();

        Assert.Equal(
            [RetentionTarget.Events, RetentionTarget.AuditRecords],
            periods.Select(p => p.Target));
    }
}
