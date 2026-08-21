namespace Viegard.Application.Queues;

/// <summary>Queue payload for incident classification work.</summary>
public readonly record struct IncidentWorkItem(Guid IncidentId);

/// <summary>Queue payload for policy evaluation work.</summary>
public readonly record struct ClassificationWorkItem(Guid ClassificationId);
