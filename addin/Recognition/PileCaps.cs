using System.Globalization;
using System.Text.RegularExpressions;

namespace Bimtech.RevitMcp.Recognition;

// What a pile cap's text says, e.g. "Đ1 (2000x2000x1200)" → name Đ1, 2000 x 2000, thickness 1200;
// "ĐM2 h=1500" → name ĐM2, thickness 1500. Null fields mean the text does not say.
public sealed record CapTag(string Text, string? Name, double? LengthMm, double? WidthMm, double? ThicknessMm);

// Pile cap helpers that need no Revit: reading cap marks, picking the mark of each cap, plan geometry in mm.
public static partial class PileCaps
{
    // Piles drawn just outside a cap outline still belong to it; further out they belong to no cap.
    public const double OwnerToleranceMm = 300;

    public static CapTag? ParseTag(string text)
    {
        var name = CapName().Match(text) is { Success: true } nameMatch ? nameMatch.Value.Replace(" ", "") : null;
        double? length = null, width = null, thickness = null;
        if (Dimensions().Match(text) is { Success: true } dims)
        {
            var a = Number(dims.Groups["a"].Value);
            var b = Number(dims.Groups["b"].Value);
            (length, width) = a >= b ? (a, b) : (b, a);
            if (dims.Groups["c"].Success) thickness = Number(dims.Groups["c"].Value);
        }
        if (Thickness().Match(text) is { Success: true } h) thickness = Number(h.Groups["h"].Value);
        return name is null && thickness is null && length is null ? null : new CapTag(text, name, length, width, thickness);
    }

    // The mark of one cap: a cap text inside its outline, else the nearest one within the cap's size from its centre
    // (marks are often written beside the cap). texts: (text, x, y) in mm; outline in mm.
    public static CapTag? PickTag(IReadOnlyList<(string Text, double X, double Y)> texts, IReadOnlyList<(double X, double Y)> outline)
    {
        var tags = texts.Select(text => (Text: text, Tag: ParseTag(text.Text))).Where(entry => entry.Tag is not null).ToList();
        if (tags.Count == 0 || outline.Count < 3) return null;
        var inside = tags.Where(entry => Contains(outline, entry.Text.X, entry.Text.Y)).Select(entry => entry.Tag).FirstOrDefault();
        if (inside is not null) return inside;

        var (cx, cy) = (outline.Average(p => p.X), outline.Average(p => p.Y));
        var size = Math.Max(outline.Max(p => p.X) - outline.Min(p => p.X), outline.Max(p => p.Y) - outline.Min(p => p.Y));
        return tags
            .Select(entry => (entry.Tag, Distance: Math.Sqrt(Math.Pow(entry.Text.X - cx, 2) + Math.Pow(entry.Text.Y - cy, 2))))
            .Where(entry => entry.Distance <= size)
            .OrderBy(entry => entry.Distance)
            .Select(entry => entry.Tag)
            .FirstOrDefault();
    }

    // Ray casting, even-odd.
    public static bool Contains(IReadOnlyList<(double X, double Y)> polygon, double x, double y)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            if ((polygon[i].Y > y) != (polygon[j].Y > y)
                && x < (polygon[j].X - polygon[i].X) * (y - polygon[i].Y) / (polygon[j].Y - polygon[i].Y) + polygon[i].X)
                inside = !inside;
        return inside;
    }

    // Shortest distance from a point to the outline's edges.
    public static double DistanceToOutline(IReadOnlyList<(double X, double Y)> polygon, double x, double y)
    {
        var best = double.MaxValue;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var (ax, ay, bx, by) = (polygon[j].X, polygon[j].Y, polygon[i].X, polygon[i].Y);
            var (dx, dy) = (bx - ax, by - ay);
            var lengthSquared = dx * dx + dy * dy;
            var t = lengthSquared == 0 ? 0 : Math.Clamp(((x - ax) * dx + (y - ay) * dy) / lengthSquared, 0, 1);
            best = Math.Min(best, Math.Sqrt(Math.Pow(x - (ax + t * dx), 2) + Math.Pow(y - (ay + t * dy), 2)));
        }
        return best;
    }

    // Nearest multiple of step (5 mm by default for piles and caps); step 0 leaves the value as it is.
    public static double RoundTo(double value, double step) =>
        step <= 0 ? value : Math.Round(value / step, MidpointRounding.AwayFromZero) * step;

    private static double Number(string text) => double.Parse(text, CultureInfo.InvariantCulture);

    // Đ1, ĐM2, ĐC3, DM1, Đ1A, M4 at the start of a word.
    [GeneratedRegex(@"(?<![\p{L}\d])(Đ[MC]?|D[MC]|M)\s?\d{1,3}[A-Za-z]?(?![\p{L}\d])", RegexOptions.IgnoreCase)]
    private static partial Regex CapName();

    [GeneratedRegex(@"(?<!\d)(?<a>\d{3,4})\s*[xX×*]\s*(?<b>\d{3,4})(\s*[xX×*]\s*(?<c>\d{3,4}))?(?!\d)")]
    private static partial Regex Dimensions();

    [GeneratedRegex(@"(?<![\p{L}])[hH]\s*[=:]?\s*(?<h>\d{3,4})(?!\d)")]
    private static partial Regex Thickness();
}
