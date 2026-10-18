using System.Text.RegularExpressions;

namespace Bimtech.RevitMcp.Recognition;

// Piles and pile caps read the way an engineer reads a foundation plan, from every layer, each candidate weighed on
// evidence with its reason so the review window can say why:
//   caps (closed outlines 400 mm - 20 m a side): + piles inside, on a grid crossing (or on one grid), a cap mark
//     ("Đ1 (2000x2000x1200)"), its size repeated, a cap-like layer name; − no pile inside, holding whole caps,
//     an empty outline inside a cap with piles (the column on it), an other-like layer name;
//   piles (circles 200-2000 mm across): + inside a cap, its diameter repeated, a pile symbol (a cross or a hatch
//     inside, or a second circle), a pile-like layer name; − a grid bubble (at a grid's end, or holding a grid's
//     name), an other-like layer name, alone (no cap, no symbol, a diameter found nowhere else).
// Layer names are a hint, never a gate. Weighed on the Evidence scale from 50%: piles inside a cap, crossings and
// supports ±20%, marks and repeats ±10%, layer names ±5%; a bubble −60%, a column inside a cap and a wall strip
// −50%, an outline holding caps −30%. Sure from 50%, to review from 20%, dropped below, still reported with its reasons. Concentric circles count as one pile, outlines drawn twice as one cap. All lengths mm.
public static partial class PileRecognizer
{
    public sealed class Options
    {
        public double MinPileMm { get; init; } = 200;
        public double MaxPileMm { get; init; } = 2000;
        public double MinCapMm { get; init; } = 400;
        public double MaxCapMm { get; init; } = 20000;
        public double SameCenterMm { get; init; } = 20;
        public double SameCapMm { get; init; } = 50;
        public int RepeatCount { get; init; } = 3;
        public double GridMarginMm { get; init; } = GridIntersections.DefaultMarginMm;
        // A circle this close to a grid's end (beyond its radius) is the grid's bubble.
        public double BubbleGapMm { get; init; } = 150;
        // An outline at most this much larger than a cap inside it is the cap's lean concrete, not a separate thing.
        public double LeanMarginMm { get; init; } = 400;
        // An outline no thicker than this on average (2·area/perimeter) is a strip of wall, not a cap.
        public double MaxStripMm { get; init; } = 400;
    }

    public sealed class Pile : Weighed
    {
        public double Cx { get; init; }
        public double Cy { get; init; }
        public double DiameterMm { get; set; }
        public List<string> Layers { get; } = [];
        public bool HasSymbol { get; set; }
        public Cap? Cap { get; set; }
    }

    public sealed class Cap : Weighed
    {
        // Closed outline, [x, y], last point not repeated.
        public List<double[]> Points { get; init; } = [];
        public double Cx { get; init; }
        public double Cy { get; init; }
        public double Length { get; init; }
        public double Width { get; init; }
        public List<string> Layers { get; } = [];
        public CapTag? Tag { get; set; }
        public string? Grid { get; set; }
        public int Piles { get; set; }
    }

    public sealed record Result(List<Pile> Piles, List<Pile> DroppedPiles, List<Cap> Caps, List<Cap> DroppedCaps);

    // Evidence, worded without names so the review window can count them over a group.
    public const string PilesInsideReason = "có cọc bên trong";
    public const string NoPileReason = "không có cọc bên trong";
    public const string CrossingReason = "chứa giao trục";
    public const string OneGridReason = "có trục đi qua";
    public const string CapTagReason = "có ký hiệu đài";
    public const string CapRepeatReason = "kích thước đài lặp lại";
    public const string CapLayerReason = "tên layer giống đài móng";
    public const string HoldsCapsReason = "bao lấy đài khác (khung nhà, ô phòng)";
    public const string InsideCapReason = "nằm trong đài có cọc (cột, cổ móng)";
    public const string StripReason = "dải mảnh (tường, vách), không phải đài";
    public const string InCapReason = "nằm trong đài";
    public const string DiameterRepeatReason = "đường kính lặp lại nhiều lần";
    public const string SymbolReason = "có ký hiệu cọc (nét chéo, hatch hay vòng thứ hai)";
    public const string PileLayerReason = "tên layer giống cọc";
    public const string BubbleReason = "vòng tròn đầu trục";
    public const string OtherLayerReason = "tên layer giống thứ khác (trục, kích thước, chữ...)";
    public const string AloneReason = "đơn lẻ: không đài, không ký hiệu, đường kính không lặp";

    // grids: the model's grids (for crossings and bubbles). texts: Text Notes and DWG texts, model mm.
    public static Result Recognize(CadCase scene, IReadOnlyList<TruthGrid> grids, WallRecognizer.Region? region, Options? options = null)
    {
        options ??= new Options();
        string Layer(int style) => style >= 0 && style < scene.Styles.Count ? scene.Styles[style].Layer : "";
        bool In(double x, double y) => region is null || region.Contains(x, y);
        var frame = BeamCase.FrameOf(grids);
        var texts = scene.Texts.Select(text => (text.Text, text.X, text.Y)).ToList();

        var piles = Circles(scene, Layer, In, options);
        var caps = Caps(scene, Layer, In, options);

        // Grid bubbles first: they are never piles, and they must not make a cap look like it holds piles.
        var gridNames = grids.Select(grid => grid.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pile in piles)
        {
            var radius = pile.DiameterMm / 2;
            var atEnd = grids.Any(grid => Distance(pile.Cx, pile.Cy, grid.X0, grid.Y0) <= radius + options.BubbleGapMm
                || Distance(pile.Cx, pile.Cy, grid.X1, grid.Y1) <= radius + options.BubbleGapMm);
            var named = texts.Any(text => gridNames.Contains(text.Text.Trim()) && Distance(pile.Cx, pile.Cy, text.X, text.Y) <= radius);
            if (atEnd || named) pile.Add(-60, BubbleReason);
        }

        var sizes = caps.GroupBy(cap => (Math.Round(Math.Min(cap.Length, cap.Width) / 50), Math.Round(Math.Max(cap.Length, cap.Width) / 50)))
            .ToDictionary(group => group.Key, group => group.Count());
        foreach (var cap in caps)
        {
            cap.Piles = piles.Count(pile => pile.Percent >= Evidence.BasePercent && Contains(cap.Points, pile.Cx, pile.Cy));
            if (cap.Piles > 0) cap.Add(20, PilesInsideReason);
            else cap.Add(-20, NoPileReason);
            var crossings = CrossingsInside(cap, frame);
            if (crossings.Count > 0)
            {
                cap.Grid = crossings[0];
                cap.Add(20, CrossingReason);
            }
            else if (GridThrough(cap, frame) is { } grid)
            {
                cap.Grid = $"trục {grid}";
                cap.Add(10, OneGridReason);
            }
            cap.Tag = PileCaps.PickTag(texts, cap.Points.Select(p => (p[0], p[1])).ToList());
            if (cap.Tag?.Name is not null) cap.Add(10, CapTagReason);
            if (sizes[(Math.Round(Math.Min(cap.Length, cap.Width) / 50), Math.Round(Math.Max(cap.Length, cap.Width) / 50))] >= 2)
                cap.Add(10, CapRepeatReason);
            // A building outline or a large room holds whole caps; a cap's lean-concrete outline is only a little larger.
            if (caps.Any(other => !ReferenceEquals(other, cap)
                    && Math.Max(other.Length, other.Width) < Math.Max(cap.Length, cap.Width) - options.LeanMarginMm
                    && other.Length * other.Width < cap.Length * cap.Width
                    && Contains(cap.Points, other.Cx, other.Cy) && piles.Any(pile => pile.Percent >= Evidence.BasePercent && Contains(other.Points, pile.Cx, pile.Cy))))
                cap.Veto(HoldsCapsReason);
            // A column or pedestal drawn inside a cap: no pile of its own, inside a larger outline that holds piles.
            if (cap.Piles == 0 && caps.Any(other => !ReferenceEquals(other, cap)
                    && other.Length * other.Width > cap.Length * cap.Width && Contains(other.Points, cap.Cx, cap.Cy)
                    && piles.Any(pile => pile.Percent >= Evidence.BasePercent && Contains(other.Points, pile.Cx, pile.Cy))))
                cap.Veto(InsideCapReason, Evidence.DropBelowPercent - 10);
            // A thin strip (an L of core walls drawn on the foundation plan) is a wall, however many piles lie near it.
            var thickness = MeanThickness(cap.Points);
            if (thickness <= options.MaxStripMm && Math.Max(cap.Length, cap.Width) >= 4 * thickness) cap.Veto(StripReason, Evidence.DropBelowPercent - 10);
            if (cap.Layers.Any(layer => CapLayer().IsMatch(layer))) cap.Add(5, CapLayerReason);
            else if (cap.Layers.Count > 0 && cap.Layers.All(layer => OtherLayer().IsMatch(layer))) cap.Add(-5, OtherLayerReason);
        }

        var diameters = piles.GroupBy(pile => Math.Round(pile.DiameterMm / 10)).ToDictionary(group => group.Key, group => group.Count());
        var hatchLoops = scene.Hatches.SelectMany(ColumnRecognizer.OuterLoops).ToList();
        foreach (var pile in piles)
        {
            // The smallest cap holding the pile: a cap drawn inside its lean-concrete outline holds it first.
            pile.Cap = caps.Where(cap => Evidence.IsKept(cap.Percent) && Contains(cap.Points, pile.Cx, pile.Cy))
                .OrderBy(cap => cap.Length * cap.Width).FirstOrDefault();
            if (pile.Cap is not null) pile.Add(20, InCapReason);
            var repeated = diameters[Math.Round(pile.DiameterMm / 10)] >= options.RepeatCount;
            if (repeated) pile.Add(10, DiameterRepeatReason);
            if (!pile.HasSymbol) pile.HasSymbol = Symbol(pile, scene, hatchLoops);
            if (pile.HasSymbol) pile.Add(10, SymbolReason);
            if (pile.Layers.Any(layer => PileLayer().IsMatch(layer))) pile.Add(5, PileLayerReason);
            else if (pile.Layers.Count > 0 && pile.Layers.All(layer => OtherLayer().IsMatch(layer))) pile.Add(-5, OtherLayerReason);
            if (pile.Cap is null && !repeated && !pile.HasSymbol) pile.Add(-10, AloneReason);
        }

        return new Result(piles.Where(p => Evidence.IsKept(p.Percent)).ToList(), piles.Where(p => !Evidence.IsKept(p.Percent)).ToList(),
            caps.Where(c => Evidence.IsKept(c.Percent)).ToList(), caps.Where(c => !Evidence.IsKept(c.Percent)).ToList());
    }

    // Circles of pile size; concentric ones are one pile (the largest), the smaller one its symbol.
    private static List<Pile> Circles(CadCase scene, Func<int, string> layer, Func<double, double, bool> inRegion, Options options)
    {
        var piles = new List<Pile>();
        foreach (var arc in scene.Arcs.Where(arc => arc.IsCircle && inRegion(arc.Cx, arc.Cy)).OrderByDescending(arc => arc.R))
        {
            var diameter = 2 * arc.R;
            var same = piles.FirstOrDefault(pile => Distance(pile.Cx, pile.Cy, arc.Cx, arc.Cy) <= options.SameCenterMm);
            if (same is not null)
            {
                // Inner circles of a pile symbol, or the same circle drawn twice.
                if (diameter < same.DiameterMm - 1) same.HasSymbol = true;
                var name = layer(arc.Style);
                if (name.Length > 0 && !same.Layers.Contains(name)) same.Layers.Add(name);
                continue;
            }
            if (diameter < options.MinPileMm || diameter > options.MaxPileMm) continue;
            var pile = new Pile { Cx = arc.Cx, Cy = arc.Cy, DiameterMm = Math.Round(diameter) };
            if (layer(arc.Style) is { Length: > 0 } first) pile.Layers.Add(first);
            piles.Add(pile);
        }
        return piles;
    }

    // Closed outlines: closed polylines, and rectangles drawn as loose lines; outlines drawn twice count once.
    private static List<Cap> Caps(CadCase scene, Func<int, string> layer, Func<double, double, bool> inRegion, Options options)
    {
        var outlines = new List<(string Layer, List<double[]> Points)>();
        foreach (var polyline in scene.Polylines.Where(p => p.Points.Count >= 3))
        {
            var points = polyline.Points.ToList();
            var closed = polyline.Closed || Distance(points[0][0], points[0][1], points[^1][0], points[^1][1]) <= 2;
            if (!closed) continue;
            if (Distance(points[0][0], points[0][1], points[^1][0], points[^1][1]) <= 2 && points.Count > 3) points.RemoveAt(points.Count - 1);
            outlines.Add((layer(polyline.Style), points));
        }
        foreach (var group in scene.Lines.Where(line => line.Length <= options.MaxCapMm + 5).GroupBy(line => line.Style))
        {
            var segments = group.Select(line => new Shapes.Segment(line.X0, line.Y0, line.X1, line.Y1)).ToList();
            foreach (var cluster in Shapes.Clusters(segments, 2).Where(cluster => cluster.Count >= 3))
            {
                var part = cluster.Select(i => segments[i]).ToList();
                if (ColumnRecognizer.Fit(part.SelectMany(s => new[] { (s.X0, s.Y0), (s.X1, s.Y1) }).ToList()) is not { } rect) continue;
                if (!Shapes.IsRectangle(part, rect)) continue;
                outlines.Add((layer(group.Key), Corners(rect)));
            }
        }

        var caps = new List<Cap>();
        foreach (var (name, points) in outlines)
        {
            if (ColumnRecognizer.Fit(points.Select(p => (p[0], p[1])).ToList()) is not { } rect) continue;
            if (Math.Min(rect.Length, rect.Width) < options.MinCapMm || Math.Max(rect.Length, rect.Width) > options.MaxCapMm) continue;
            if (!inRegion(rect.Cx, rect.Cy)) continue;
            var same = caps.FirstOrDefault(cap => Distance(cap.Cx, cap.Cy, rect.Cx, rect.Cy) <= options.SameCapMm
                && Math.Abs(Math.Min(cap.Length, cap.Width) - Math.Min(rect.Length, rect.Width)) <= options.SameCapMm
                && Math.Abs(Math.Max(cap.Length, cap.Width) - Math.Max(rect.Length, rect.Width)) <= options.SameCapMm);
            if (same is null)
            {
                same = new Cap { Points = points, Cx = rect.Cx, Cy = rect.Cy, Length = rect.Length, Width = rect.Width };
                caps.Add(same);
            }
            if (name.Length > 0 && !same.Layers.Contains(name)) same.Layers.Add(name);
        }
        return caps;
    }

    // A cross through the centre (two lines ending on or near the circle) or a hatch filling it.
    private static bool Symbol(Pile pile, CadCase scene, IReadOnlyList<List<double[]>> hatchLoops)
    {
        var r = pile.DiameterMm / 2;
        var through = scene.Lines.Count(line => line.Length >= r && line.Length <= 2 * r + 40
            && Distance(line.X0, line.Y0, pile.Cx, pile.Cy) <= r + 20 && Distance(line.X1, line.Y1, pile.Cx, pile.Cy) <= r + 20
            && DistanceToSegment(pile.Cx, pile.Cy, line) <= 20);
        if (through >= 2) return true;
        return hatchLoops.Any(loop => loop.All(p => Distance(p[0], p[1], pile.Cx, pile.Cy) <= r + 20) && Inside(loop, pile.Cx, pile.Cy));
    }

    // The grid crossings inside the cap's outline, as "A-1".
    private static List<string> CrossingsInside(Cap cap, BeamCase.Frame frame)
    {
        var found = new List<string>();
        foreach (var gu in frame.AlongU)
            foreach (var gv in frame.AlongV)
            {
                var (x, y) = frame.Model(gv.PositionMm, gu.PositionMm);
                if (Contains(cap.Points, x, y)) found.Add(GridIntersections.Label(gu.Name, gv.Name));
            }
        return found;
    }

    // A grid passing through the cap (its line crossing the outline's extent across it).
    private static string? GridThrough(Cap cap, BeamCase.Frame frame)
    {
        var us = cap.Points.Select(p => frame.U(p[0], p[1])).ToList();
        var vs = cap.Points.Select(p => frame.V(p[0], p[1])).ToList();
        return frame.AlongV.FirstOrDefault(g => g.PositionMm >= us.Min() && g.PositionMm <= us.Max())?.Name
            ?? frame.AlongU.FirstOrDefault(g => g.PositionMm >= vs.Min() && g.PositionMm <= vs.Max())?.Name;
    }

    private static List<double[]> Corners(Shapes.Rectangle rect)
    {
        var (cos, sin) = (Math.Cos(rect.AngleRadians), Math.Sin(rect.AngleRadians));
        return new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) }
            .Select(c => (U: c.Item1 * rect.Length / 2, V: c.Item2 * rect.Width / 2))
            .Select(c => new[] { rect.Cx + c.U * cos - c.V * sin, rect.Cy + c.U * sin + c.V * cos }).ToList();
    }

    // 2·area/perimeter: the thickness of a strip, about half the smaller side of a rectangle.
    private static double MeanThickness(IReadOnlyList<double[]> loop)
    {
        double area = 0, perimeter = 0;
        for (var i = 0; i < loop.Count; i++)
        {
            var (a, b) = (loop[i], loop[(i + 1) % loop.Count]);
            area += a[0] * b[1] - b[0] * a[1];
            perimeter += Distance(a[0], a[1], b[0], b[1]);
        }
        return perimeter < 1e-9 ? 0 : Math.Abs(area) / perimeter;
    }

    private static double Distance(double x0, double y0, double x1, double y1) => Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));

    private static double DistanceToSegment(double x, double y, CadLine line)
    {
        var (dx, dy) = (line.X1 - line.X0, line.Y1 - line.Y0);
        var length2 = dx * dx + dy * dy;
        var t = length2 < 1e-9 ? 0 : Math.Clamp(((x - line.X0) * dx + (y - line.Y0) * dy) / length2, 0, 1);
        return Distance(x, y, line.X0 + t * dx, line.Y0 + t * dy);
    }

    // Inside the outline, or within PileCaps.OwnerToleranceMm of it: piles drawn just outside still belong to the cap.
    private static bool Contains(IReadOnlyList<double[]> polygon, double x, double y)
    {
        if (polygon.Count < 3) return false;
        var inside = false;
        var nearest = double.MaxValue;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            if ((polygon[i][1] > y) != (polygon[j][1] > y)
                && x < (polygon[j][0] - polygon[i][0]) * (y - polygon[i][1]) / (polygon[j][1] - polygon[i][1]) + polygon[i][0])
                inside = !inside;
            nearest = Math.Min(nearest, DistanceToSegment(x, y, new CadLine { X0 = polygon[j][0], Y0 = polygon[j][1], X1 = polygon[i][0], Y1 = polygon[i][1] }));
        }
        return inside || nearest <= PileCaps.OwnerToleranceMm;
    }

    // Strictly inside, for hatches filling a circle.
    private static bool Inside(IReadOnlyList<double[]> polygon, double x, double y)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            if ((polygon[i][1] > y) != (polygon[j][1] > y)
                && x < (polygon[j][0] - polygon[i][0]) * (y - polygon[i][1]) / (polygon[j][1] - polygon[i][1]) + polygon[i][0])
                inside = !inside;
        return inside;
    }

    // "COC", "CỌC", "PILE", "COC KHOAN NHOI".
    [GeneratedRegex(@"c[oọ]c|pile|khoan\s?nh[oồ]i|bored", RegexOptions.IgnoreCase)]
    private static partial Regex PileLayer();

    // "DAI", "ĐÀI", "MONG", "MÓNG", "FOOTING", "PILECAP", "CAP".
    [GeneratedRegex(@"(?<![a-z])([dđ][aà]i|m[oó]ng|foot\w*|cap)(?![a-z])|pilecap", RegexOptions.IgnoreCase)]
    private static partial Regex CapLayer();

    [GeneratedRegex(@"tr[uụ]c|axis|grid|dim|text|anno|title|khung\s?t[eê]n|logo|c[uử]a|door|window", RegexOptions.IgnoreCase)]
    private static partial Regex OtherLayer();
}
