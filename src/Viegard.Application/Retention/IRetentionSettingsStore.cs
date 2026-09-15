namespace Viegard.Application.Retention;

/// <summary>Persistence port for runtime-owned retention settings and cycle status.</summary>
public interface IRetentionSettingsStore
{
    ValueTask<RetentionSettings?> GetAsync(CancellationToken cancellationToken = default);

    ValueTask<RetentionSettingsSaveResult> UpsertAsync(
        RetentionSettings settings,
        int expectedVersion,
        string updatedBy,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    ValueTask<RetentionSettings?> SeedIfMissingAsync(
        RetentionOptions options,
        DateTimeOffset seededAt,
        CancellationToken cancellationToken = default);

    ValueTask UpdateLastCycleAsync(
        DateTimeOffset lastCycleAt,
        IReadOnlyDictionary<RetentionTarget, long> counts,
        CancellationToken cancellationToken = default);
}

public enum RetentionSettingsSaveStatus
{
    Saved,
    Conflict,
}

public sealed record RetentionSettingsSaveResult(
    RetentionSettingsSaveStatus Status,
    RetentionSettings? Settings)
{
    public bool Succeeded => Status == RetentionSettingsSaveStatus.Saved;

    public static RetentionSettingsSaveResult Saved(RetentionSettings settings) =>
        new(RetentionSettingsSaveStatus.Saved, settings);

    public static RetentionSettingsSaveResult Conflict(RetentionSettings? current) =>
        new(RetentionSettingsSaveStatus.Conflict, current);
}
