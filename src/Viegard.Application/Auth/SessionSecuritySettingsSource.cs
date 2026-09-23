namespace Viegard.Application.Auth;

public interface ISessionSecuritySettingsDiagnostics
{
    void RefreshFailed(Exception exception);
}

public sealed class NullSessionSecuritySettingsDiagnostics : ISessionSecuritySettingsDiagnostics
{
    public static NullSessionSecuritySettingsDiagnostics Instance { get; } = new();

    private NullSessionSecuritySettingsDiagnostics()
    {
    }

    public void RefreshFailed(Exception exception)
    {
    }
}

/// <summary>
/// Holds the current session-security settings snapshot and refreshes it when
/// the durable row changes.  Mirrors <c>IncidentCoalescingSettingsSource</c>.
/// The step-up gate and the pending-action capture read the live values through
/// this source so admin edits take effect without a restart.
/// </summary>
public sealed class SessionSecuritySettingsSource(
    ISessionSecuritySettingsStore? settingsStore = null,
    ISessionSecuritySettingsDiagnostics? diagnostics = null)
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);
    private readonly ISessionSecuritySettingsDiagnostics _diagnostics = diagnostics ?? NullSessionSecuritySettingsDiagnostics.Instance;
    private SessionSecuritySettingsSnapshot _snapshot = SessionSecuritySettingsSnapshot.Unseeded;

    public SessionSecuritySettingsSnapshot Current => Volatile.Read(ref _snapshot);

    public SessionSecurityValues CurrentValues(SessionSecurityOptions fallbackOptions) =>
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
                    ? SessionSecuritySettingsSnapshot.Unseeded
                    : SessionSecuritySettingsSnapshot.Seeded(settings));
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

public sealed record SessionSecuritySettingsSnapshot(
    bool IsSeeded,
    int StepUpValiditySeconds,
    int ResumeStashTtlSeconds,
    int RowVersion)
{
    public static SessionSecuritySettingsSnapshot Unseeded { get; } = new(
        false,
        0,
        0,
        0);

    public static SessionSecuritySettingsSnapshot Seeded(SessionSecuritySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new SessionSecuritySettingsSnapshot(
            true,
            settings.StepUpValiditySeconds,
            settings.ResumeStashTtlSeconds,
            settings.RowVersion);
    }

    public SessionSecurityValues ValuesOrFallback(SessionSecurityOptions fallbackOptions) =>
        IsSeeded
            ? new SessionSecurityValues(
                StepUpValiditySeconds,
                ResumeStashTtlSeconds)
            : SessionSecurityValues.FromOptions(fallbackOptions);
}
