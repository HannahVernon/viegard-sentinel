namespace Viegard.Domain.Events;

/// <summary>The kind of entity a normalized event refers to.</summary>
public enum EntityKind
{
    IpAddress,
    Subnet,
    EmailAddress,
    Host,
    Uri,
    UserAgent,
    UserIdentity,
    FileName,
    Other,
}

/// <summary>
/// A reference to an entity involved in an event.  Entity values originate
/// from observed data and are therefore untrusted input.
/// </summary>
public readonly record struct EntityRef(EntityKind Kind, string Value);
