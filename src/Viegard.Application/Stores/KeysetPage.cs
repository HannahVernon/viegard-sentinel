namespace Viegard.Application.Stores;

public sealed record KeysetPage<T>(IReadOnlyList<T> Items, Guid? NextCursor, long TotalCount, long Preceding);
