namespace Viegard.Application.Configuration;

public interface IMikroTikRouterStore
{
    ValueTask<IReadOnlyList<MikroTikRouter>> ListAsync(CancellationToken cancellationToken = default);

    ValueTask<MikroTikRouter?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    ValueTask<MikroTikRouterSaveResult> CreateAsync(
        MikroTikRouter router,
        string passwordCiphertext,
        CancellationToken cancellationToken = default);

    ValueTask<MikroTikRouterSaveResult> UpdateAsync(
        MikroTikRouter router,
        int expectedRowVersion,
        string? passwordCiphertext = null,
        CancellationToken cancellationToken = default);

    ValueTask<MikroTikRouterDeleteResult> DeleteAsync(
        Guid id,
        int expectedRowVersion,
        CancellationToken cancellationToken = default);

    ValueTask<string?> GetCredentialCiphertextAsync(Guid id, CancellationToken cancellationToken = default);
}

public enum MikroTikRouterSaveStatus
{
    Saved,
    Conflict,
    DuplicateName,
    NotFound,
}

public sealed record MikroTikRouterSaveResult(
    MikroTikRouterSaveStatus Status,
    MikroTikRouter? Router)
{
    public bool Succeeded => Status == MikroTikRouterSaveStatus.Saved;

    public static MikroTikRouterSaveResult Saved(MikroTikRouter router) =>
        new(MikroTikRouterSaveStatus.Saved, router);

    public static MikroTikRouterSaveResult Conflict(MikroTikRouter? current) =>
        new(MikroTikRouterSaveStatus.Conflict, current);

    public static MikroTikRouterSaveResult DuplicateName(MikroTikRouter? current = null) =>
        new(MikroTikRouterSaveStatus.DuplicateName, current);

    public static MikroTikRouterSaveResult NotFound() =>
        new(MikroTikRouterSaveStatus.NotFound, null);
}

public enum MikroTikRouterDeleteStatus
{
    Deleted,
    Conflict,
    NotFound,
}

public sealed record MikroTikRouterDeleteResult(
    MikroTikRouterDeleteStatus Status,
    MikroTikRouter? Current)
{
    public bool Succeeded => Status == MikroTikRouterDeleteStatus.Deleted;

    public static MikroTikRouterDeleteResult Deleted() =>
        new(MikroTikRouterDeleteStatus.Deleted, null);

    public static MikroTikRouterDeleteResult Conflict(MikroTikRouter current) =>
        new(MikroTikRouterDeleteStatus.Conflict, current);

    public static MikroTikRouterDeleteResult NotFound() =>
        new(MikroTikRouterDeleteStatus.NotFound, null);
}
