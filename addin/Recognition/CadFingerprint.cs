using System.Globalization;

namespace Bimtech.RevitMcp.Recognition;

// A compact summary of a CadCase, small enough for Claude to read the whole drawing at once and say which layer
// holds what: per layer the entity counts, line patterns, typical sizes (circle diameters, rectangle sizes,
// spacing of parallel line pairs), plus the most frequent texts and the best grid candidate.
public sealed record CadFingerprint(
    Extent ExtentMm, FingerprintTotals Totals, IReadOnlyList<LayerFingerprint> Layers, IReadOnlyList<TextSample> Texts,
    GridStyleScore? GridCandidate, IReadOnlyDictionary<string, int> Skipped);

public sealed record Extent(double MinX, double MinY, double MaxX, double MaxY);

public sealed record FingerprintTotals(int Lines, int Arcs, int Circles, int Polylines, int ClosedPolylines, int Texts);

public sealed record LayerFingerprint(
    string Layer, IReadOnlyList<string> LinePatterns, IReadOnlyList<string> Colors, int Lines, int LongLines,
    double AxisAlignedShare, LengthStats? LineLengthMm, int Arcs, int Circles, IReadOnlyList<SizeCount> CircleDiametersMm,
    int ClosedPolylines, IReadOnlyList<SizeCount> RectanglesMm, IReadOnlyList<SizeCount> ParallelPairWidthsMm);

public sealed record LengthStats(double Median, double P90, double Max);

public sealed record SizeCount(string Size, int Count);

public sealed record TextSample(string Text, int Count);

public static class CadFingerprintBuilder
{
    private const int TopSizes = 6;
    private const int TopTexts = 40;
    private const double LongLineMm = 1000;
    private const double AxisTolerance = 0.01;
    private const double MinPairWidthMm = 30;
    private const double MaxPairWidthMm = 600;
    // Pair search is quadratic in the worst case; very busy layers are skipped.
    private const int MaxLinesForPairs = 5000;

    public static CadFingerprint Build(CadCase data)
    {
        var layers = data.Styles
            .Select((style, index) => (style, index))
            .GroupBy(entry => entry.style.Layer, StringComparer.OrdinalIgnoreCase)
            .Select(group => Layer(data, group.Key, group.Select(entry => entry.index).ToHashSet(),
                group.Select(entry => entry.style).ToList()))
            .Where(layer => layer.Lines + layer.Arcs + layer.Circles + layer.ClosedPolylines > 0)
            .OrderByDescending(layer => layer.Lines + layer.Arcs + layer.Circles + layer.ClosedPolylines)
            .ToList();

        var texts = data.Texts
            .Where(text => text.Text.Length > 0)
            .GroupBy(text => text.Text)
            .Select(group => new TextSample(group.Key, group.Count()))
            .OrderByDescending(sample => sample.Count).ThenBy(sample => sample.Text, StringComparer.Ordinal)
            .Take(TopTexts)
            .ToList();

        return new CadFingerprint(
            Extent(data),
            new FingerprintTotals(data.Lines.Count, data.Arcs.Count(arc => !arc.IsCircle), data.Arcs.Count(arc => arc.IsCircle),
                data.Polylines.Count, data.Polylines.Count(polyline => polyline.Closed), data.Texts.Count),
            layers,
            texts,
            GridRecognizer.Recognize(data).Styles.FirstOrDefault(),
            data.Skipped);
    }

    private static LayerFingerprint Layer(CadCase data, string layer, HashSet<int> styles, List<CadStyle> info)
    {
        var lines = data.Lines.Where(line => styles.Contains(line.Style)).ToList();
        var arcs = data.Arcs.Where(arc => styles.Contains(arc.Style)).ToList();
        var closed = data.Polylines.Where(polyline => polyline.Closed && styles.Contains(polyline.Style)).ToList();
        var lengths = lines.Select(line => line.Length).Order().ToList();

        return new LayerFingerprint(
            layer,
            info.Select(style => style.LinePattern ?? "(none)").Distinct().ToList(),
            info.Where(style => style.Color is not null).Select(style => style.Color!).Distinct().ToList(),
            lines.Count,
            lines.Count(line => line.Length >= LongLineMm),
            lines.Count == 0 ? 0 : Math.Round((double)lines.Count(IsAxisAligned) / lines.Count, 2),
            lengths.Count == 0 ? null : new LengthStats(Round(Percentile(lengths, 0.5)), Round(Percentile(lengths, 0.9)), Round(lengths[^1])),
            arcs.Count(arc => !arc.IsCircle),
            arcs.Count(arc => arc.IsCircle),
            Top(arcs.Where(arc => arc.IsCircle).Select(arc => Mm(2 * arc.R))),
            closed.Count,
            Top(closed.Select(RectangleSize).OfType<string>()),
            lines.Count <= MaxLinesForPairs ? Top(PairWidths(lines).Select(Mm)) : []);
    }

    // "300x500" (short side first, rounded to 5 mm) when the closed polyline is an axis-aligned or rotated rectangle.
    private static string? RectangleSize(CadPolyline polyline)
    {
        if (polyline.Points.Count != 4) return null;
        var p = polyline.Points;
        double Side(int a, int b) => Math.Sqrt(Math.Pow(p[b][0] - p[a][0], 2) + Math.Pow(p[b][1] - p[a][1], 2));
        var (s0, s1, s2, s3) = (Side(0, 1), Side(1, 2), Side(2, 3), Side(3, 0));
        var diagonals = Math.Abs(Side(0, 2) - Side(1, 3));
        if (Math.Abs(s0 - s2) > 2 || Math.Abs(s1 - s3) > 2 || diagonals > 2) return null;
        var (small, large) = s0 <= s1 ? (s0, s1) : (s1, s0);
        return $"{Mm(small)}x{Mm(large)}";
    }

    // Distances between parallel axis-aligned lines that overlap over at least half the shorter one: the widths of
    // beams and walls drawn as two lines.
    private static IEnumerable<double> PairWidths(List<CadLine> lines)
    {
        foreach (var vertical in new[] { true, false })
        {
            var ordered = lines.Where(IsAxisAligned)
                .Where(line => IsVertical(line) == vertical)
                .Select(line => vertical
                    ? (At: (line.X0 + line.X1) / 2, Min: Math.Min(line.Y0, line.Y1), Max: Math.Max(line.Y0, line.Y1))
                    : (At: (line.Y0 + line.Y1) / 2, Min: Math.Min(line.X0, line.X1), Max: Math.Max(line.X0, line.X1)))
                .OrderBy(line => line.At)
                .ToList();
            for (var i = 0; i < ordered.Count; i++)
                for (var j = i + 1; j < ordered.Count && ordered[j].At - ordered[i].At <= MaxPairWidthMm; j++)
                {
                    var gap = ordered[j].At - ordered[i].At;
                    if (gap < MinPairWidthMm) continue;
                    var overlap = Math.Min(ordered[i].Max, ordered[j].Max) - Math.Max(ordered[i].Min, ordered[j].Min);
                    var shorter = Math.Min(ordered[i].Max - ordered[i].Min, ordered[j].Max - ordered[j].Min);
                    if (shorter > 0 && overlap >= 0.5 * shorter) yield return gap;
                }
        }
    }

    // The most frequent sizes, most frequent first.
    private static List<SizeCount> Top<T>(IEnumerable<T> sizes) where T : notnull =>
        sizes.Select(size => Convert.ToString(size, CultureInfo.InvariantCulture)!)
            .GroupBy(size => size)
            .Select(group => new SizeCount(group.Key, group.Count()))
            .OrderByDescending(entry => entry.Count).ThenBy(entry => entry.Size, StringComparer.Ordinal)
            .Take(TopSizes)
            .ToList();

    private static Extent Extent(CadCase data)
    {
        var xs = data.Lines.SelectMany(line => new[] { line.X0, line.X1 })
            .Concat(data.Arcs.SelectMany(arc => new[] { arc.Cx - arc.R, arc.Cx + arc.R }))
            .Concat(data.Polylines.SelectMany(polyline => polyline.Points.Select(point => point[0]))).ToList();
        var ys = data.Lines.SelectMany(line => new[] { line.Y0, line.Y1 })
            .Concat(data.Arcs.SelectMany(arc => new[] { arc.Cy - arc.R, arc.Cy + arc.R }))
            .Concat(data.Polylines.SelectMany(polyline => polyline.Points.Select(point => point[1]))).ToList();
        return xs.Count == 0 ? new Extent(0, 0, 0, 0) : new Extent(Round(xs.Min()), Round(ys.Min()), Round(xs.Max()), Round(ys.Max()));
    }

    private static bool IsAxisAligned(CadLine line) =>
        line.Length > 0 && (Math.Abs(line.X1 - line.X0) / line.Length <= AxisTolerance || Math.Abs(line.Y1 - line.Y0) / line.Length <= AxisTolerance);

    private static bool IsVertical(CadLine line) => Math.Abs(line.X1 - line.X0) <= Math.Abs(line.Y1 - line.Y0);

    private static double Percentile(List<double> sorted, double share) => sorted[(int)Math.Min(sorted.Count - 1, share * sorted.Count)];

    // Sizes are reported rounded to 5 mm, like the scanners group them.
    private static double Mm(double value) => Math.Round(value / 5, MidpointRounding.AwayFromZero) * 5;

    private static double Round(double value) => Math.Round(value, 0);
}
