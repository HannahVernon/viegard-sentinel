using Viegard.Application.Configuration;

namespace Viegard.Persistence.InMemory;

public sealed class InMemoryMikroTikRouterStore : IMikroTikRouterStore
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, StoredRouter> _routers = [];

    public ValueTask<IReadOnlyList<MikroTikRouter>> ListAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return ValueTask.FromResult<IReadOnlyList<MikroTikRouter>>(
                _routers.Values
                    .Select(stored => stored.Router)
                    .OrderBy(router => router.Name, StringComparer.Ordinal)
                    .ToList());
        }
    }

    public ValueTask<MikroTikRouter?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return ValueTask.FromResult(
                _routers.TryGetValue(id, out var stored) ? stored.Router : null);
        }
    }

    public ValueTask<MikroTikRouterSaveResult> CreateAsync(
        MikroTikRouter router,
        string passwordCiphertext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(router);
        var normalized = MikroTikRouterValidator.NormalizeForSave(router) with { RowVersion = 1 };
        ValidatePasswordCiphertext(passwordCiphertext);

        lock (_sync)
        {
            if (_routers.ContainsKey(normalized.Id))
            {
                return ValueTask.FromResult(MikroTikRouterSaveResult.Conflict(_routers[normalized.Id].Router));
            }

            if (NameExists(normalized.Name, exceptId: null))
            {
                return ValueTask.FromResult(MikroTikRouterSaveResult.DuplicateName());
            }

            _routers.Add(normalized.Id, new StoredRouter(normalized, passwordCiphertext));
            return ValueTask.FromResult(MikroTikRouterSaveResult.Saved(normalized));
        }
    }

    public ValueTask<MikroTikRouterSaveResult> UpdateAsync(
        MikroTikRouter router,
        int expectedRowVersion,
        string? passwordCiphertext = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRowVersion);
        var normalized = MikroTikRouterValidator.NormalizeForSave(router);
        if (passwordCiphertext is not null)
        {
            ValidatePasswordCiphertext(passwordCiphertext);
        }

        lock (_sync)
        {
            if (!_routers.TryGetValue(normalized.Id, out var existing))
            {
                return ValueTask.FromResult(MikroTikRouterSaveResult.NotFound());
            }

            if (existing.Router.RowVersion != expectedRowVersion)
            {
                return ValueTask.FromResult(MikroTikRouterSaveResult.Conflict(existing.Router));
            }

            if (NameExists(normalized.Name, normalized.Id))
            {
                return ValueTask.FromResult(MikroTikRouterSaveResult.DuplicateName(existing.Router));
            }

            var updated = normalized with
            {
                CreatedAt = existing.Router.CreatedAt,
                RowVersion = existing.Router.RowVersion + 1,
            };
            _routers[updated.Id] = new StoredRouter(updated, passwordCiphertext ?? existing.PasswordCiphertext);
            return ValueTask.FromResult(MikroTikRouterSaveResult.Saved(updated));
        }
    }

    public ValueTask<MikroTikRouterDeleteResult> DeleteAsync(
        Guid id,
        int expectedRowVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRowVersion);

        lock (_sync)
        {
            if (!_routers.TryGetValue(id, out var existing))
            {
                return ValueTask.FromResult(MikroTikRouterDeleteResult.NotFound());
            }

            if (existing.Router.RowVersion != expectedRowVersion)
            {
                return ValueTask.FromResult(MikroTikRouterDeleteResult.Conflict(existing.Router));
            }

            _routers.Remove(id);
            return ValueTask.FromResult(MikroTikRouterDeleteResult.Deleted());
        }
    }

    public ValueTask<string?> GetCredentialCiphertextAsync(Guid id, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return ValueTask.FromResult(
                _routers.TryGetValue(id, out var stored) ? stored.PasswordCiphertext : null);
        }
    }

    private bool NameExists(string name, Guid? exceptId) =>
        _routers.Values.Any(stored =>
            string.Equals(stored.Router.Name, name, StringComparison.Ordinal)
            && stored.Router.Id != exceptId);

    private static void ValidatePasswordCiphertext(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("Router password ciphertext is required.");
        }
    }

    private sealed record StoredRouter(MikroTikRouter Router, string PasswordCiphertext);
}
