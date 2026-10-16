using System.Globalization;
using System.Text.RegularExpressions;

namespace Bimtech.RevitMcp.Recognition;

// A column mark, e.g. "C1 300x500" → name C1, 300 x 500 (small side first). Null fields: the text does not say.
public sealed record ColumnTag(string Text, string? Name, double? SizeAMm, double? SizeBMm);

public static partial class ColumnTags
{
    // Marks are written beside the column; further than this they belong to no column.
    public const double MaxDistanceMm = 1500;

    public static ColumnTag? Parse(string text)
    {
        var name = Name().Match(text) is { Success: true } match ? match.Value.Replace(" ", "").ToUpperInvariant() : null;
        if (name is null) return null;
        if (Dimensions().Match(text) is not { Success: true } dims) return new ColumnTag(text, name, null, null);
        var a = double.Parse(dims.Groups["a"].Value, CultureInfo.InvariantCulture);
        var b = double.Parse(dims.Groups["b"].Value, CultureInfo.InvariantCulture);
        return new ColumnTag(text, name, Math.Min(a, b), Math.Max(a, b));
    }

    // Each text goes to the column nearest to it (within maxDistanceMm); each column keeps the nearest of its texts.
    // A mark between two columns thus labels only the closer one instead of both.
    public static T?[] AssignNearest<T>(
        IReadOnlyList<(double X, double Y)> targets, IReadOnlyList<(string Text, double X, double Y)> texts,
        Func<string, T?> parse, double maxDistanceMm) where T : class
    {
        var best = new (T? Tag, double Distance)[targets.Count];
        for (var i = 0; i < best.Length; i++) best[i] = (null, double.MaxValue);
        if (targets.Count == 0) return [];
        foreach (var text in texts)
        {
            if (parse(text.Text) is not { } tag) continue;
            var (index, distance) = targets
                .Select((target, i) => (i, Math.Sqrt(Math.Pow(target.X - text.X, 2) + Math.Pow(target.Y - text.Y, 2))))
                .MinBy(entry => entry.Item2);
            if (distance <= maxDistanceMm && distance < best[index].Distance) best[index] = (tag, distance);
        }
        return best.Select(entry => entry.Tag).ToArray();
    }

    // C1, C12A, CT3, CỘT 4 → "C1", "C12A", "CT3", "CỘT4".
    [GeneratedRegex(@"(?<![\p{L}\d])(CỘT|COT|CT|C)\s?\d{1,3}[A-Za-z]?(?![\p{L}\d])", RegexOptions.IgnoreCase)]
    private static partial Regex Name();

    [GeneratedRegex(@"(?<!\d)(?<a>\d{3,4})\s*[xX×*]\s*(?<b>\d{3,4})(?!\d)")]
    private static partial Regex Dimensions();
}
