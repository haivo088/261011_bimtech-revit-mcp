using System.Globalization;
using System.Text;

namespace Bimtech.RevitMcp.Recognition;

// One grid read from a drawing. NameSource is "bubble" when the name was read from the text in the grid's end
// circle, "auto" when it was numbered 1, 2, 3... (vertical, left to right) or A, B, C... (horizontal, bottom up).
public sealed record RecognizedGrid(
    string Name, double X0, double Y0, double X1, double Y1, bool Vertical, string NameSource, double Confidence,
    IReadOnlyList<string> Reasons);

// How strongly one graphics style looks like grid lines, with the evidence.
public sealed record GridStyleScore(
    int Style, string Layer, string? LinePattern, int VerticalCount, int HorizontalCount, int BubbleCount, double Score,
    IReadOnlyList<string> Reasons);

public sealed record GridRecognition(
    IReadOnlyList<RecognizedGrid> Grids, IReadOnlyList<GridStyleScore> Styles, IReadOnlyList<int> ChosenStyles,
    IReadOnlyList<string> Warnings);

// Finds the grid lines of a drawing. Grids are drawn with their own line pattern (center / dash-dot), so the
// line pattern is the main evidence; circles with a short text at the line ends (grid bubbles) come next and
// also give the real grid names; the layer name only helps. Candidates are long horizontal or vertical lines.
public static class GridRecognizer
{
    public const double MinLengthMm = 1000;
    // A candidate spans at least this share of the drawing in its direction.
    public const double MinRelativeLength = 0.3;
    public const double MergeToleranceMm = 10;
    public const double MinScore = 3;
    private const double AxisTolerance = 0.01;
    private const double MinBubbleRadiusMm = 100;
    private const double MaxBubbleRadiusMm = 1500;
    private const int MaxBubbleTextLength = 6;
    // More candidate lines than this on one style looks like hatching or detail, not grids.
    private const int MaxPlausibleGrids = 80;

    private sealed record Candidate(CadLine Line, bool Vertical)
    {
        public double Coordinate => Vertical ? (Line.X0 + Line.X1) / 2 : (Line.Y0 + Line.Y1) / 2;
        public double Min => Vertical ? Math.Min(Line.Y0, Line.Y1) : Math.Min(Line.X0, Line.X1);
        public double Max => Vertical ? Math.Max(Line.Y0, Line.Y1) : Math.Max(Line.X0, Line.X1);
    }

    private sealed record Axis(bool Vertical, double Coordinate, double Min, double Max)
    {
        public (double X, double Y) Start => Vertical ? (Coordinate, Min) : (Min, Coordinate);
        public (double X, double Y) End => Vertical ? (Coordinate, Max) : (Max, Coordinate);
    }

    public static GridRecognition Recognize(CadCase data)
    {
        var warnings = new List<string>();
        if (data.Lines.Count == 0)
            return new GridRecognition([], [], [], ["Bản vẽ không có line nào."]);

        var (width, height) = Extent(data.Lines);
        var circles = data.Arcs.Where(arc => arc.IsCircle && arc.R is >= MinBubbleRadiusMm and <= MaxBubbleRadiusMm).ToList();

        var scores = new List<GridStyleScore>();
        var axesByStyle = new Dictionary<int, List<Axis>>();
        foreach (var group in data.Lines.GroupBy(line => line.Style))
        {
            var candidates = Candidates(group, width, height);
            if (candidates.Count == 0) continue;
            var axes = Merge(candidates);
            axesByStyle[group.Key] = axes;
            scores.Add(Score(group.Key, data.Styles.ElementAtOrDefault(group.Key), axes, circles));
        }
        scores = scores.OrderByDescending(score => score.Score).ToList();

        var best = scores.FirstOrDefault();
        if (best is null || best.Score < MinScore)
        {
            warnings.Add("Không style nào đủ tín hiệu của trục (nét tâm, đầu trục tròn có tên, hoặc tên layer GRID/TRỤC/AXIS).");
            return new GridRecognition([], scores, [], warnings);
        }
        var chosen = scores.Where(score => score.Score >= Math.Max(MinScore, best.Score * 0.75)).Select(score => score.Style).ToList();

        var axesAll = Merge(chosen.SelectMany(style => axesByStyle[style]).Select(axis =>
            new Candidate(axis.Vertical
                ? new CadLine { X0 = axis.Coordinate, Y0 = axis.Min, X1 = axis.Coordinate, Y1 = axis.Max }
                : new CadLine { X0 = axis.Min, Y0 = axis.Coordinate, X1 = axis.Max, Y1 = axis.Coordinate }, axis.Vertical)).ToList());
        if (!axesAll.Any(axis => axis.Vertical) || !axesAll.Any(axis => !axis.Vertical))
            warnings.Add("Chỉ tìm thấy trục theo một phương.");

        var grids = Name(axesAll, circles, data.Texts, best.Score, warnings);
        return new GridRecognition(grids, scores, chosen, warnings);
    }

    private static (double Width, double Height) Extent(IReadOnlyList<CadLine> lines)
    {
        var minX = lines.Min(line => Math.Min(line.X0, line.X1));
        var maxX = lines.Max(line => Math.Max(line.X0, line.X1));
        var minY = lines.Min(line => Math.Min(line.Y0, line.Y1));
        var maxY = lines.Max(line => Math.Max(line.Y0, line.Y1));
        return (maxX - minX, maxY - minY);
    }

    private static List<Candidate> Candidates(IEnumerable<CadLine> lines, double width, double height)
    {
        var result = new List<Candidate>();
        foreach (var line in lines)
        {
            var length = line.Length;
            if (length < MinLengthMm) continue;
            if (Math.Abs(line.X1 - line.X0) / length <= AxisTolerance && length >= MinRelativeLength * height)
                result.Add(new Candidate(line, true));
            else if (Math.Abs(line.Y1 - line.Y0) / length <= AxisTolerance && length >= MinRelativeLength * width)
                result.Add(new Candidate(line, false));
        }
        return result;
    }

    // Lines on the same coordinate (within MergeToleranceMm) become one axis; the longest line sets the coordinate.
    private static List<Axis> Merge(IReadOnlyList<Candidate> candidates)
    {
        var axes = new List<Axis>();
        foreach (var direction in candidates.GroupBy(candidate => candidate.Vertical))
        {
            var ordered = direction.OrderBy(candidate => candidate.Coordinate).ToList();
            var cluster = new List<Candidate>();
            foreach (var candidate in ordered)
            {
                if (cluster.Count > 0 && candidate.Coordinate - cluster[^1].Coordinate > MergeToleranceMm)
                {
                    axes.Add(ToAxis(cluster));
                    cluster = [];
                }
                cluster.Add(candidate);
            }
            if (cluster.Count > 0) axes.Add(ToAxis(cluster));
        }
        return axes;

        static Axis ToAxis(List<Candidate> cluster)
        {
            var longest = cluster.MaxBy(candidate => candidate.Line.Length)!;
            return new Axis(longest.Vertical, longest.Coordinate, cluster.Min(c => c.Min), cluster.Max(c => c.Max));
        }
    }

    private static GridStyleScore Score(int style, CadStyle? info, IReadOnlyList<Axis> axes, IReadOnlyList<CadArc> circles)
    {
        var reasons = new List<string>();
        var score = 0.0;
        var vertical = axes.Count(axis => axis.Vertical);
        var horizontal = axes.Count - vertical;

        if (IsGridPattern(info?.LinePattern))
        {
            score += 3;
            reasons.Add($"Nét \"{info!.LinePattern}\" là nét trục.");
        }
        var bubbles = axes.Count(axis => FindBubble(axis, circles) is not null);
        if (bubbles > 0)
        {
            var share = (double)bubbles / axes.Count;
            score += 4 * share;
            reasons.Add($"{bubbles}/{axes.Count} đường có vòng tròn đầu trục.");
        }
        if (IsGridName(info?.Layer))
        {
            score += 2;
            reasons.Add($"Tên layer \"{info!.Layer}\" gợi ý trục.");
        }
        if (vertical >= 2 && horizontal >= 2)
        {
            score += 1;
            reasons.Add($"Có {vertical} đường đứng và {horizontal} đường ngang dài.");
        }
        if (axes.Count > MaxPlausibleGrids)
        {
            score -= 2;
            reasons.Add($"{axes.Count} đường: quá nhiều cho một hệ trục.");
        }
        return new GridStyleScore(style, info?.Layer ?? "", info?.LinePattern, vertical, horizontal, bubbles,
            Math.Round(score, 2), reasons);
    }

    // A grid bubble: a circle at one end of the axis, its center on the axis line (or its extension).
    private static CadArc? FindBubble(Axis axis, IReadOnlyList<CadArc> circles)
    {
        CadArc? best = null;
        var bestDistance = double.MaxValue;
        foreach (var end in new[] { axis.Start, axis.End })
            foreach (var circle in circles)
            {
                var along = axis.Vertical ? circle.Cy - end.Y : circle.Cx - end.X;
                var across = axis.Vertical ? circle.Cx - axis.Coordinate : circle.Cy - axis.Coordinate;
                var distance = Math.Abs(along);
                if (Math.Abs(across) <= circle.R * 0.5 && distance <= circle.R * 2.2 && distance < bestDistance)
                {
                    best = circle;
                    bestDistance = distance;
                }
            }
        return best;
    }

    private static List<RecognizedGrid> Name(
        IReadOnlyList<Axis> axes, IReadOnlyList<CadArc> circles, IReadOnlyList<CadText> texts, double styleScore,
        List<string> warnings)
    {
        var named = axes.Select(axis =>
        {
            var bubble = FindBubble(axis, circles);
            var text = bubble is null ? null : texts
                .Where(text => text.Text.Length is > 0 and <= MaxBubbleTextLength)
                .Select(text => (Text: text, Distance: Math.Sqrt(Math.Pow(text.X - bubble.Cx, 2) + Math.Pow(text.Y - bubble.Cy, 2))))
                .Where(entry => entry.Distance <= bubble.R * 1.5)
                .OrderBy(entry => entry.Distance)
                .Select(entry => entry.Text.Text.Trim())
                .FirstOrDefault();
            return (Axis: axis, Bubble: bubble, Name: text);
        }).ToList();

        var used = named.Where(entry => entry.Name is not null).Select(entry => entry.Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var duplicate in named.Where(entry => entry.Name is not null).GroupBy(entry => entry.Name!, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
            warnings.Add($"Tên trục \"{duplicate.Key}\" xuất hiện {duplicate.Count()} lần.");

        var result = new List<RecognizedGrid>();
        foreach (var vertical in new[] { true, false })
        {
            var index = 0;
            foreach (var entry in named.Where(entry => entry.Axis.Vertical == vertical).OrderBy(entry => entry.Axis.Coordinate))
            {
                var reasons = new List<string>();
                var name = entry.Name;
                var source = "bubble";
                if (name is null)
                {
                    do name = vertical ? (++index).ToString(CultureInfo.InvariantCulture) : Letters(index++);
                    while (used.Contains(name));
                    used.Add(name);
                    source = "auto";
                    reasons.Add(entry.Bubble is null ? "Không có vòng tròn đầu trục; tên tự đánh số." : "Không đọc được tên trong vòng tròn; tên tự đánh số.");
                }
                var confidence = Math.Clamp(styleScore / 10 + (entry.Bubble is null ? 0 : 0.2) - (source == "auto" ? 0.1 : 0), 0, 1);
                var (start, end) = (entry.Axis.Start, entry.Axis.End);
                result.Add(new RecognizedGrid(name, start.X, start.Y, end.X, end.Y, vertical, source, Math.Round(confidence, 2), reasons));
            }
        }
        return result;
    }

    // 0 → A, 25 → Z, 26 → AA.
    private static string Letters(int index)
    {
        var value = index + 1;
        var text = "";
        while (value > 0)
        {
            value--;
            text = (char)('A' + value % 26) + text;
            value /= 26;
        }
        return text;
    }

    // CENTER, Center 2x, DASHDOT, Dash Dot, CHAIN, "TRỤC"... Revit names imported patterns after the DWG linetype.
    internal static bool IsGridPattern(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        var text = Normalize(pattern).Replace(" ", "").Replace("_", "").Replace("-", "");
        return text.Contains("CENTER") || text.Contains("CENTRE") || text.Contains("DASHDOT") || text.Contains("CHAIN")
            || text.Contains("TRUC") || text.Contains("AXIS") || text.Contains("GRID");
    }

    internal static bool IsGridName(string? layer)
    {
        if (string.IsNullOrWhiteSpace(layer)) return false;
        var text = Normalize(layer);
        return text.Contains("GRID") || text.Contains("TRUC") || text.Contains("AXIS");
    }

    // Upper case without Vietnamese diacritics, so "Trục" matches "TRUC".
    private static string Normalize(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(c);
        return builder.ToString().Normalize(NormalizationForm.FormC).Replace('Đ', 'D').Replace('đ', 'd').ToUpperInvariant();
    }
}
