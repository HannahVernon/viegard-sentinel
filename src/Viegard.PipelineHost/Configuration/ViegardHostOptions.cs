namespace Viegard.PipelineHost.Configuration;

/// <summary>Well-known pipeline role names.</summary>
public static class RoleNames
{
    public const string Sources = "sources";
    public const string Correlation = "correlation";
    public const string Classification = "classification";
    public const string Policy = "policy";
    public const string Actions = "actions";
}

/// <summary>
/// Host-instance configuration: which pipeline roles this process runs
/// (D-0011).  The correlator and policy/action engine are singleton roles;
/// deploying them in more than one instance is a configuration error that
/// must be caught by deployment validation.
/// </summary>
public sealed class ViegardHostOptions
{
    public const string SectionName = "Viegard:Host";

    public static readonly IReadOnlySet<string> KnownRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        RoleNames.Sources,
        RoleNames.Correlation,
        RoleNames.Classification,
        RoleNames.Policy,
        RoleNames.Actions,
    };

    /// <summary>Identifier for this host instance; defaults to the machine name when empty.</summary>
    public string? InstanceId { get; set; }

    /// <summary>The pipeline roles this instance runs.  Must be a non-empty subset of <see cref="KnownRoles"/>.</summary>
    public IList<string> Roles { get; } = [];

    public string EffectiveInstanceId =>
        string.IsNullOrWhiteSpace(InstanceId) ? Environment.MachineName : InstanceId;
}
