namespace Bimtech.RevitMcp.Commands.Common;

// "limit" and "offset" params shared by every list command.
public readonly record struct PageRequest(int Offset, int Limit)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 500;

    public static PageRequest From(CommandParams parameters, int defaultLimit = DefaultLimit, int maxLimit = MaxLimit) =>
        new(parameters.GetInt("offset", 0, min: 0), parameters.GetInt("limit", defaultLimit, min: 1, max: maxLimit));
}

public static class Paging
{
    // Enumerates the source once: items inside the page are converted, the rest are only counted.
    // The source must have a stable order (e.g. sorted by ID) so consecutive pages do not overlap.
    public static object ToPage<T>(
        IEnumerable<T> source, PageRequest page, Func<T, object> select, CancellationToken cancellation)
    {
        var items = new List<object>(Math.Min(page.Limit, 256));
        var total = 0;
        foreach (var item in source)
        {
            if (total % 1000 == 0) cancellation.ThrowIfCancellationRequested();
            if (total >= page.Offset && items.Count < page.Limit) items.Add(select(item));
            total++;
        }
        var end = page.Offset + items.Count;
        return new
        {
            total,
            offset = page.Offset,
            limit = page.Limit,
            returned = items.Count,
            hasMore = end < total,
            nextOffset = end < total ? end : (int?)null,
            items,
        };
    }
}
