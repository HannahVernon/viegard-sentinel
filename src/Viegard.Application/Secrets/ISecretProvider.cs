namespace Viegard.Application.Secrets;

/// <summary>
/// A secret value.  The value is only obtainable via <see cref="Reveal"/> so
/// accidental logging, serialization, or string interpolation exposes the
/// name, never the value.
/// </summary>
public sealed class Secret
{
    private readonly string _value;

    public Secret(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        Name = name;
        _value = value;
    }

    public string Name { get; }

    /// <summary>Explicitly retrieve the secret value.  Never log or persist the result.</summary>
    public string Reveal() => _value;

    public override string ToString() => $"Secret[{Name}]";
}

/// <summary>
/// Secret retrieval port (Roost).  Implementations: mounted secret files
/// (production), .NET user-secrets via configuration (development); Vault,
/// SOPS/age, OS stores, or cloud managers can be added without changing
/// application code (D-0006).  Secret values must never reach logs, prompts,
/// exception messages, telemetry, audit records, or documentation.
/// </summary>
public interface ISecretProvider
{
    /// <summary>Retrieve a named secret, or null when it does not exist.</summary>
    Task<Secret?> GetAsync(string name, CancellationToken cancellationToken = default);
}
