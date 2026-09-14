using Viegard.Application.Stores;

namespace Viegard.Persistence.Postgres.Stores;

internal sealed record PostgresSortDefinition(
    string SelectSql,
    string CursorFromSql,
    string OrderExpression,
    string CursorOrderExpression,
    string IdExpression,
    string CursorIdExpression);

internal readonly record struct ActivePostgresSort(
    PostgresSortDefinition Definition,
    string DirectionSql,
    string PageComparator,
    string PrecedingComparator);

internal static class PostgresKeysetSorting
{
    public static bool TryCreate<TColumn>(
        ListSort<TColumn>? sort,
        Func<TColumn, PostgresSortDefinition?> getDefinition,
        out ActivePostgresSort activeSort)
        where TColumn : struct, Enum
    {
        if (sort is not { } safeSort
            || !TryGetDirection(safeSort.Direction, out var directionSql, out var pageComparator, out var precedingComparator)
            || getDefinition(safeSort.Column) is not { } definition)
        {
            activeSort = default;
            return false;
        }

        activeSort = new ActivePostgresSort(definition, directionSql, pageComparator, precedingComparator);
        return true;
    }

    public static void AddSeekCondition(
        ICollection<string> conditions,
        IList<object?> args,
        ActivePostgresSort activeSort,
        string comparator,
        Guid? cursorId)
    {
        if (cursorId is null)
        {
            return;
        }

        conditions.Add(
            $"({activeSort.Definition.OrderExpression}, {activeSort.Definition.IdExpression}) {comparator} "
            + $"(SELECT {activeSort.Definition.CursorOrderExpression}, {activeSort.Definition.CursorIdExpression} "
            + $"{activeSort.Definition.CursorFromSql} "
            + $"WHERE {activeSort.Definition.CursorIdExpression} = {{{args.Count}}})");
        args.Add(cursorId.Value);
    }

    public static string OrderByClause(ActivePostgresSort activeSort) =>
        $" ORDER BY {activeSort.Definition.OrderExpression} {activeSort.DirectionSql}, "
        + $"{activeSort.Definition.IdExpression} {activeSort.DirectionSql}";

    private static bool TryGetDirection(
        SortDirection direction,
        out string directionSql,
        out string pageComparator,
        out string precedingComparator)
    {
        switch (direction)
        {
            case SortDirection.Asc:
                directionSql = "ASC";
                pageComparator = ">";
                precedingComparator = "<";
                return true;
            case SortDirection.Desc:
                directionSql = "DESC";
                pageComparator = "<";
                precedingComparator = ">";
                return true;
            default:
                directionSql = string.Empty;
                pageComparator = string.Empty;
                precedingComparator = string.Empty;
                return false;
        }
    }
}
