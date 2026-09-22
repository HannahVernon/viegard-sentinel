using Viegard.Application.Burst;
using Viegard.Domain.Events;
using Viegard.Persistence.InMemory;

namespace Viegard.Application.Tests;

public sealed class BurstDetectionTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Validator_accepts_default_settings()
    {
        Assert.True(BurstDetectionSettingsValidator.TryValidate(new BurstDetectionSettings(), out var error));
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData(0, 300, 3600, BurstDetectionSettingsValidator.ThresholdError)]
    [InlineData(5, 0, 3600, BurstDetectionSettingsValidator.WindowError)]
    [InlineData(5, 300, -1, BurstDetectionSettingsValidator.CooldownError)]
    public void Validator_rejects_out_of_range_values(int threshold, int window, int cooldown, string expected)
    {
        var settings = new BurstDetectionSettings
        {
            AuthFailureThreshold = threshold,
            AuthFailureWindowSeconds = window,
            AuthFailureCooldownSeconds = cooldown,
        };

        Assert.False(BurstDetectionSettingsValidator.TryValidate(settings, out var error));
        Assert.Equal(expected, error);
    }

    [Theory]
    [InlineData(AdminAuthEventKind.LoginFailed, true)]
    [InlineData(AdminAuthEventKind.TotpFailed, true)]
    [InlineData(AdminAuthEventKind.StepUpFailed, true)]
    [InlineData(AdminAuthEventKind.WebAuthnFailed, true)]
    [InlineData(AdminAuthEventKind.LoginSucceeded, false)]
    [InlineData(AdminAuthEventKind.LockoutTriggered, false)]
    public void AuthFailureSignal_matches_only_failure_kinds(AdminAuthEventKind kind, bool expected)
    {
        var signal = new AuthFailureBurstSignal();
        Assert.Equal(expected, signal.Matches(AuthEvent(kind, "203.0.113.7")));
    }

    [Fact]
    public void AuthFailureSignal_prefers_entity_ip_then_remote_address()
    {
        var signal = new AuthFailureBurstSignal();

        var withEntity = AuthEvent(AdminAuthEventKind.LoginFailed, "203.0.113.7", entityIp: "198.51.100.9");
        Assert.Equal("ip=198.51.100.9", signal.SourceKey(withEntity));

        var withoutEntity = AuthEvent(AdminAuthEventKind.LoginFailed, "203.0.113.7");
        Assert.Equal("ip=203.0.113.7", signal.SourceKey(withoutEntity));
    }

    [Fact]
    public async Task Engine_fires_once_when_threshold_reached_and_suppresses_within_cooldown()
    {
        var detector = Detector(new BurstDetectionOptions
        {
            AuthFailureThreshold = 3,
            AuthFailureWindowSeconds = 60,
            AuthFailureCooldownSeconds = 3600,
        });

        Assert.Empty(await detector.ObserveAsync(AuthEvent(AdminAuthEventKind.LoginFailed, "203.0.113.7", at: Base)));
        Assert.Empty(await detector.ObserveAsync(AuthEvent(AdminAuthEventKind.LoginFailed, "203.0.113.7", at: Base.AddSeconds(10))));

        var fired = await detector.ObserveAsync(AuthEvent(AdminAuthEventKind.LoginFailed, "203.0.113.7", at: Base.AddSeconds(20)));
        var firing = Assert.Single(fired);
        Assert.Equal(BurstSignalIds.AuthFailure, firing.SignalId);
        Assert.Equal("ip=203.0.113.7", firing.SourceKey);
        Assert.Equal(3, firing.Count);
        Assert.Equal(3, firing.EventIds.Count);
        Assert.False(firing.ActionEligible);

        Assert.Empty(await detector.ObserveAsync(AuthEvent(AdminAuthEventKind.LoginFailed, "203.0.113.7", at: Base.AddSeconds(30))));
    }

    [Fact]
    public async Task Engine_does_not_fire_when_occurrences_fall_outside_window()
    {
        var detector = Detector(new BurstDetectionOptions
        {
            AuthFailureThreshold = 3,
            AuthFailureWindowSeconds = 60,
            AuthFailureCooldownSeconds = 3600,
        });

        await detector.ObserveAsync(AuthEvent(AdminAuthEventKind.LoginFailed, "203.0.113.7", at: Base));
        await detector.ObserveAsync(AuthEvent(AdminAuthEventKind.LoginFailed, "203.0.113.7", at: Base.AddSeconds(10)));

        var fired = await detector.ObserveAsync(AuthEvent(AdminAuthEventKind.LoginFailed, "203.0.113.7", at: Base.AddSeconds(200)));
        Assert.Empty(fired);
    }

    [Fact]
    public async Task Engine_counts_per_source_independently()
    {
        var detector = Detector(new BurstDetectionOptions
        {
            AuthFailureThreshold = 2,
            AuthFailureWindowSeconds = 60,
            AuthFailureCooldownSeconds = 3600,
        });

        await detector.ObserveAsync(AuthEvent(AdminAuthEventKind.LoginFailed, "203.0.113.7", at: Base));
        var otherSource = await detector.ObserveAsync(AuthEvent(AdminAuthEventKind.LoginFailed, "198.51.100.9", at: Base.AddSeconds(1)));
        Assert.Empty(otherSource);

        var fired = await detector.ObserveAsync(AuthEvent(AdminAuthEventKind.LoginFailed, "203.0.113.7", at: Base.AddSeconds(2)));
        Assert.Single(fired);
    }

    [Fact]
    public async Task Engine_respects_global_and_signal_switches()
    {
        var globalOff = Detector(new BurstDetectionOptions { GlobalEnabled = false, AuthFailureThreshold = 1 });
        Assert.Empty(await globalOff.ObserveAsync(AuthEvent(AdminAuthEventKind.LoginFailed, "203.0.113.7", at: Base)));

        var signalOff = Detector(new BurstDetectionOptions { AuthFailureEnabled = false, AuthFailureThreshold = 1 });
        Assert.Empty(await signalOff.ObserveAsync(AuthEvent(AdminAuthEventKind.LoginFailed, "203.0.113.7", at: Base)));
    }

    [Fact]
    public async Task Engine_marks_action_eligible_when_configured()
    {
        var detector = Detector(new BurstDetectionOptions
        {
            AuthFailureThreshold = 1,
            AuthFailureActionEligible = true,
        });

        var fired = await detector.ObserveAsync(AuthEvent(AdminAuthEventKind.LoginFailed, "203.0.113.7", at: Base));
        Assert.True(Assert.Single(fired).ActionEligible);
    }

    [Fact]
    public async Task Source_reflects_seeded_values_and_falls_back_when_unseeded()
    {
        var store = new InMemoryBurstDetectionSettingsStore();
        var source = new BurstDetectionSettingsSource(store);

        await source.RefreshAsync();
        Assert.False(source.Current.IsSeeded);
        Assert.Equal(9, source.CurrentValues(new BurstDetectionOptions { AuthFailureThreshold = 9 }).AuthFailureThreshold);

        await store.TryCreateAsync(new BurstDetectionSettings { AuthFailureThreshold = 12 } with { UpdatedAt = Base });
        await source.RefreshAsync();

        Assert.True(source.Current.IsSeeded);
        Assert.Equal(12, source.CurrentValues(new BurstDetectionOptions()).AuthFailureThreshold);
    }

    private static BurstDetector Detector(BurstDetectionOptions options) => new(
        [new AuthFailureBurstSignal()],
        new InMemoryBurstWindowStore(),
        new BurstDetectionSettingsSource(),
        options);

    private static NormalizedEvent AuthEvent(
        AdminAuthEventKind kind,
        string remoteAddress,
        string? entityIp = null,
        DateTimeOffset? at = null)
    {
        var occurredAt = at ?? Base;
        IReadOnlyList<EntityRef> entities = entityIp is null
            ? []
            : [new EntityRef(EntityKind.IpAddress, entityIp)];

        return new NormalizedEvent
        {
            Id = Guid.NewGuid(),
            SourceId = "admin",
            SourceType = "admin-auth",
            OccurredAt = occurredAt,
            Entities = entities,
            Payload = new AdminAuthEvent
            {
                Kind = kind,
                Username = "operator",
                RemoteAddress = remoteAddress,
                UserAgent = "test-agent",
                OccurredAt = occurredAt,
            },
            RawObservationId = Guid.NewGuid(),
        };
    }
}
