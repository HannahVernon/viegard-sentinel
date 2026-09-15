namespace Viegard.AdminApi.Components.Shared;

public static class PaginationMath
{
    public static long PageNumber(long preceding, int take)
    {
        var safeTake = Math.Max(1, take);
        return Math.Max(1, preceding / safeTake + 1);
    }

    public static long TotalPages(long totalCount, int take)
    {
        var safeTake = Math.Max(1, take);
        return Math.Max(1, (long)Math.Ceiling(totalCount / (double)safeTake));
    }
}
