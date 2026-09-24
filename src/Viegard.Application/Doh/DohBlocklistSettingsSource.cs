namespace Viegard.Application.Doh;

public interface IDohBlocklistSettingsDiagnostics
{
    void RefreshFailed(Exception exception);
}

public sealed class NullDohBlocklistSettingsDiagnostics : IDohBlocklistSettingsDiagnostics
{
    public static NullDohBlocklistSettingsDiagnostics Instance { get; } = new();

    private NullDohBlocklistSettingsDiagnostics()
    {
    }

    public void RefreshFailed(Exception exception)
    {
    }
}

/// <summary>
/// Holds the current DoH blocklist settings snapshot and refreshes it when the
/// durable row changes.  Mirrors <c>BurstDetectionSettingsSource</c>.
/// </summary>
public sealed class DohBlocklistSettingsSource(
    IDohBlocklistSettingsStore? settingsStore = null,
    IDohBlocklistSettingsDiagnostics? diagnostics = null)
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);
    private readonly IDohBlocklistSettingsDiagnostics _diagnostics = diagnostics ?? NullDohBlocklistSettingsDiagnostics.Instance;
    private DohBlocklistSettingsSnapshot _snapshot = DohBlocklistSettingsSnapshot.Unseeded;

    public DohBlocklistSettingsSnapshot Current => Volatile.Read(ref _snapshot);

    public DohBlocklistValues CurrentValues(DohBlocklistOptions fallbackOptions) =>
        Current.ValuesOrFallback(fallbackOptions);

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (settingsStore is null)
        {
            return;
        }

        try
        {
            var settings = await settingsStore.GetAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(
                ref _snapshot,
                settings is null
                    ? DohBlocklistSettingsSnapshot.Unseeded
                    : DohBlocklistSettingsSnapshot.Seeded(settings));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _diagnostics.RefreshFailed(ex);
        }
    }

    public async Task RunRefreshLoopAsync(CancellationToken cancellationToken = default)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (settingsStore is null)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return;
        }

        var seenVersion = settingsStore.CurrentChangeVersion;
        while (!cancellationToken.IsCancellationRequested)
        {
            seenVersion = await settingsStore
                .WaitForChangeAsync(seenVersion, RefreshInterval, cancellationToken)
                .ConfigureAwait(false);
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

public sealed record DohBlocklistSettingsSnapshot(
    bool IsSeeded,
    bool Enabled,
    string PrimaryFeedUrl,
    string SecondaryFeedUrl,
    string AddressListName,
    int FetchIntervalSeconds,
    bool ProbeEnabled,
    string ProbeCanaryFqdn,
    string ProbeExpectedToken,
    string ProbeEndpointPath,
    int ProbeTimeoutSeconds,
    int ProbeConcurrency,
    int ProbeIntervalSeconds,
    bool ApplyToRouters,
    int RowVersion)
{
    public static DohBlocklistSettingsSnapshot Unseeded { get; } = new(
        false,
        false,
        string.Empty,
        string.Empty,
        DohBlocklistSettings.DefaultAddressListName,
        0,
        false,
        string.Empty,
        string.Empty,
        string.Empty,
        0,
        0,
        0,
        false,
        0);

    public static DohBlocklistSettingsSnapshot Seeded(DohBlocklistSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new DohBlocklistSettingsSnapshot(
            true,
            settings.Enabled,
            settings.PrimaryFeedUrl,
            settings.SecondaryFeedUrl,
            settings.AddressListName,
            settings.FetchIntervalSeconds,
            settings.ProbeEnabled,
            settings.ProbeCanaryFqdn,
            settings.ProbeExpectedToken,
            settings.ProbeEndpointPath,
            settings.ProbeTimeoutSeconds,
            settings.ProbeConcurrency,
            settings.ProbeIntervalSeconds,
            settings.ApplyToRouters,
            settings.RowVersion);
    }

    public DohBlocklistValues ValuesOrFallback(DohBlocklistOptions fallbackOptions) =>
        IsSeeded
            ? new DohBlocklistValues(
                Enabled,
                PrimaryFeedUrl,
                SecondaryFeedUrl,
                AddressListName,
                FetchIntervalSeconds,
                ProbeEnabled,
                ProbeCanaryFqdn,
                ProbeExpectedToken,
                ProbeEndpointPath,
                ProbeTimeoutSeconds,
                ProbeConcurrency,
                ProbeIntervalSeconds,
                ApplyToRouters)
            : DohBlocklistValues.FromOptions(fallbackOptions);
}
