using System.Text.RegularExpressions;

namespace Bimtech.RevitMcp.Recognition;

// Columns read the way an engineer reads a plan, from every layer: a column is a small rectangle (100-2000 mm a side)
// drawn as an outline or a hatch, and whether it really is a column is weighed from evidence, each with its reason so
// the review window can say why:
//   + at a grid crossing (or on one grid), filled with a hatch, its size repeated over the plan, a column mark beside
//     it, a column or pile cap of the storey below under it, a column-like layer name;
//   − inside a wall hatch, a layer named like something else (doors, furniture, dimensions...), and alone: on no
//     grid, without hatch and of a size found nowhere else.
// Layer names are a hint, never a gate. Weighed on the Evidence scale: from 50% (no evidence either way), strong
// relations ±20%, supporting evidence ±10%, layer names ±5%, a wall cut by doors −50%; sure from 50%, to review from
// 20%, dropped below, still
// reported with its reasons. The same column drawn twice (outline and hatch, or on two layers) counts once.
// All lengths are model mm.
public static partial class ColumnRecognizer
{
    public sealed class Options
    {
        public double MinSideMm { get; init; } = 100;
        public double MaxSideMm { get; init; } = 2000;
        public double GridMarginMm { get; init; } = GridIntersections.DefaultMarginMm;
        public double GridToleranceMm { get; init; } = GridIntersections.DefaultToleranceMm;
        // Two shapes are one column when their centres are this close and their sides agree within SameSizeMm.
        public double SameCenterMm { get; init; } = 50;
        public double SameSizeMm { get; init; } = 20;
        // A size found this many times is a repeated column size.
        public int RepeatCount { get; init; } = 3;
        // A hatch fills its rectangle at least this much to count as a rectangle.
        public double HatchFill { get; init; } = 0.9;
        public double SupportOffsetMm { get; init; } = 50;
        // Hatches thinner than this on average are walls.
        public double MaxWallMm { get; init; } = 600;
        // Pieces of a wall further apart than this are not one wall cut by a door.
        public double MaxOpeningMm { get; init; } = 2500;
    }

    // A rectangle found in the drawing: from an outline (polyline or touching lines) or from a hatch's outer loop.
    public sealed record Shape(string Layer, double Cx, double Cy, double Length, double Width, double AngleRadians, bool IsHatch);

    // Percent and signed reasons, e.g. "+20% ở giao trục", from Weighed.
    public sealed class Column : Weighed
    {
        public double Cx { get; init; }
        public double Cy { get; init; }
        // Length along AngleRadians, Width across; AngleRadians in [0, π/2).
        public double Length { get; init; }
        public double Width { get; init; }
        public double AngleRadians { get; init; }
        public List<string> Layers { get; } = [];
        public bool HasOutline { get; set; }
        public bool HasHatch { get; set; }
        // "A-1" at a grid crossing, "trục 3" on one grid, else null.
        public string? Grid { get; set; }
        public ColumnTag? Tag { get; set; }

        public double SizeA => Math.Round(Math.Min(Length, Width) / 5) * 5;
        public double SizeB => Math.Round(Math.Max(Length, Width) / 5) * 5;
    }

    // HatchOnly: hatches of column size on no outline, not taken as columns.
    public sealed record Result(List<Column> Columns, List<Column> Dropped, int Shapes, int Hatches, int HatchOnly);

    // Evidence, worded without names so the review window can count them over a group.
    public const string CrossingReason = "ở giao trục";
    public const string OneGridReason = "nằm trên 1 trục";
    public const string HatchReason = "có hatch bên trong";
    public const string RepeatReason = "kích thước lặp lại nhiều lần";
    public const string TagReason = "có ký hiệu cột bên cạnh";
    public const string BelowReason = "có cột hoặc đài móng tầng dưới";
    public const string ColumnLayerReason = "tên layer giống cột";
    public const string InWallReason = "nằm trong hatch vách";
    public const string OtherLayerReason = "tên layer giống thứ khác (cửa, thiết bị, kích thước...)";
    public const string AloneReason = "đơn lẻ: không trục, không hatch, không ký hiệu, kích thước không lặp";
    public const string WallRunReason = "thẳng hàng với mảnh cùng bề dày: đoạn vách bị cửa cắt";

    // Shapes of column size in a drawing: closed polylines, clusters of touching lines on one layer, and hatches.
    public static List<Shape> FindShapes(CadCase scene, WallRecognizer.Region? region, Options? options = null)
    {
        options ??= new Options();
        var shapes = new List<Shape>();
        string Layer(int style) => style >= 0 && style < scene.Styles.Count ? scene.Styles[style].Layer : "";
        void Add(string layer, IReadOnlyList<Recognition.Shapes.Segment> segments, bool checkOutline)
        {
            if (Fit(segments.SelectMany(s => new[] { (s.X0, s.Y0), (s.X1, s.Y1) }).ToList()) is not { } rect) return;
            if (!InSize(rect, options) || (region is not null && !region.Contains(rect.Cx, rect.Cy))) return;
            if (checkOutline && !Recognition.Shapes.IsRectangle(segments, rect)) return;
            shapes.Add(new Shape(layer, rect.Cx, rect.Cy, rect.Length, rect.Width, rect.AngleRadians, false));
        }

        foreach (var polyline in scene.Polylines)
        {
            var points = polyline.Points;
            if (points.Count < 3) continue;
            // A polyline left open by one side still draws a rectangle; an arrow of two sides does not.
            var segments = points.Zip(points.Skip(1).Append(points[0]))
                .Select(pair => new Recognition.Shapes.Segment(pair.First[0], pair.First[1], pair.Second[0], pair.Second[1]))
                .Where(segment => segment.Length > 1e-6).ToList();
            Add(Layer(polyline.Style), segments, checkOutline: true);
        }

        // Columns drawn as four loose lines: lines of one layer whose ends touch. Lines longer than a column side
        // (grids, beams, walls) never belong to a column outline, and leaving them out keeps the clusters small.
        foreach (var layer in scene.Lines.Where(line => line.Length <= options.MaxSideMm + 5).GroupBy(line => line.Style))
        {
            var segments = layer.Select(line => new Recognition.Shapes.Segment(line.X0, line.Y0, line.X1, line.Y1)).ToList();
            foreach (var cluster in Recognition.Shapes.Clusters(segments, 2))
                if (cluster.Count >= 2) Add(Layer(layer.Key), cluster.Select(i => segments[i]).ToList(), checkOutline: true);
        }

        // One hatch often fills many columns at once: each of its outer loops is a shape.
        foreach (var hatch in scene.Hatches)
            foreach (var outer in OuterLoops(hatch))
            {
                if (Fit(outer.Select(p => (p[0], p[1])).ToList()) is not { } rect) continue;
                if (!InSize(rect, options) || (region is not null && !region.Contains(rect.Cx, rect.Cy))) continue;
                if (Math.Abs(Area(outer)) < options.HatchFill * rect.Length * rect.Width) continue;
                shapes.Add(new Shape(Layer(hatch.Style), rect.Cx, rect.Cy, rect.Length, rect.Width, rect.AngleRadians, true));
            }
        return shapes;
    }

    // The loops of a hatch that no other of its loops encloses: separate filled areas, not holes.
    public static List<List<double[]>> OuterLoops(CadHatch hatch) =>
        hatch.Loops.Where(loop => loop.Count >= 3 && !hatch.Loops.Any(other => !ReferenceEquals(other, loop) && other.Count >= 3
            && Math.Abs(Area(other)) > Math.Abs(Area(loop)) && Contains(other, loop[0][0], loop[0][1], 0))).ToList();

    // texts: Text Notes and DWG texts in model mm. below: columns and pile caps of the storey below (their plan
    // outlines). hatchLoops: outer loops of the drawing's hatches (OuterLoops), to tell a column from a piece of a wall.
    public static Result Recognize(
        IReadOnlyList<Shape> shapes, BeamCase.Frame frame, IReadOnlyList<(string Text, double X, double Y)> texts,
        IReadOnlyList<CaseSupport> below, IReadOnlyList<List<double[]>> hatchLoops, Options? options = null)
    {
        options ??= new Options();
        // A hatch confirms an outline; alone it is not a column. Hatches read from the DWG can be misplaced (inserts
        // mirrored and rotated inside other blocks), while outlines come from Revit's own reading of the CAD.
        var merged = Merge(shapes, options);
        var columns = merged.Where(column => column.HasOutline).ToList();
        var hatchOnly = merged.Count - columns.Count;

        var sizes = columns.GroupBy(column => (column.SizeA, column.SizeB)).ToDictionary(group => group.Key, group => group.Count());
        var tags = ColumnTags.AssignNearest(columns.Select(column => (column.Cx, column.Cy)).ToList(), texts,
            ColumnTags.Parse, ColumnTags.MaxDistanceMm);
        // Wall hatches: longer than a column and thin (mean thickness 2·area/perimeter), unlike slab or zone hatches.
        var wallLoops = hatchLoops
            .Select(loop => (Loop: loop, Rect: Fit(loop.Select(p => (p[0], p[1])).ToList()), Thickness: 2 * Math.Abs(Area(loop)) / Perimeter(loop)))
            .Where(w => w.Rect is { } rect && Math.Max(rect.Length, rect.Width) > options.MaxSideMm && w.Thickness <= options.MaxWallMm)
            .ToList();

        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            column.Tag = tags[i];
            var onGrid = Grid(column, frame, options);
            if (onGrid == 2) column.Add(20, CrossingReason);
            else if (onGrid == 1) column.Add(10, OneGridReason);
            if (column.HasHatch) column.Add(20, HatchReason);
            var repeated = sizes[(column.SizeA, column.SizeB)] >= options.RepeatCount;
            if (repeated) column.Add(10, RepeatReason);
            if (column.Tag?.Name is not null) column.Add(10, TagReason);
            var onSupport = below.Any(support => Contains(support.Points, column.Cx, column.Cy, options.SupportOffsetMm));
            if (onSupport) column.Add(20, BelowReason);
            if (column.Layers.Any(layer => ColumnLayer().IsMatch(layer))) column.Add(5, ColumnLayerReason);
            else if (column.Layers.Count > 0 && column.Layers.All(layer => OtherLayer().IsMatch(layer))) column.Add(-5, OtherLayerReason);
            // A piece of the wall: no wider than the wall it sits in. A wider column is one the wall runs into.
            if (wallLoops.Any(wall => Math.Min(column.Length, column.Width) <= wall.Thickness + options.SameSizeMm
                    && Contains(wall.Loop, column.Cx, column.Cy, 0)))
                column.Add(-20, InWallReason);
            if (onGrid == 0 && !column.HasHatch && !repeated && column.Tag?.Name is null && !onSupport) column.Add(-10, AloneReason);
            if (InWallRun(column, columns, options)) column.Veto(WallRunReason);
        }
        return new Result(columns.Where(column => Evidence.IsKept(column.Percent)).ToList(), columns.Where(column => !Evidence.IsKept(column.Percent)).ToList(),
            shapes.Count(shape => !shape.IsHatch), shapes.Count(shape => shape.IsHatch), hatchOnly);
    }

    // A wall cut by doors: pieces of one thickness in a row on one line, a door's width apart. Columns of one size
    // stand a bay apart, so they never form such a row.
    private static bool InWallRun(Column column, IReadOnlyList<Column> columns, Options options)
    {
        var (thick, longSide) = (Math.Min(column.Length, column.Width), Math.Max(column.Length, column.Width));
        if (longSide < 2 * thick) return false;
        var axis = column.Length >= column.Width ? column.AngleRadians : column.AngleRadians + Math.PI / 2;
        var (cos, sin) = (Math.Cos(axis), Math.Sin(axis));
        foreach (var other in columns)
        {
            if (ReferenceEquals(other, column)) continue;
            var (otherThick, otherLong) = (Math.Min(other.Length, other.Width), Math.Max(other.Length, other.Width));
            if (Math.Abs(otherThick - thick) > options.SameSizeMm || otherLong < 2 * otherThick) continue;
            var otherAxis = other.Length >= other.Width ? other.AngleRadians : other.AngleRadians + Math.PI / 2;
            if (Math.Abs(Math.Sin(otherAxis - axis)) > Math.Sin(2 * Math.PI / 180)) continue;
            var (dx, dy) = (other.Cx - column.Cx, other.Cy - column.Cy);
            var along = dx * cos + dy * sin;
            var across = -dx * sin + dy * cos;
            var gap = Math.Abs(along) - (longSide + otherLong) / 2;
            if (Math.Abs(across) <= options.SameSizeMm && gap >= -options.SameSizeMm && gap <= options.MaxOpeningMm) return true;
        }
        return false;
    }

    // A whole case: shapes from its geometry, grids from its truth, texts as drawn. The case's supports are the
    // columns reaching its level (its truth), not the storey below, so they are not passed as evidence.
    public static Result Run(CadCase data, Options? options = null)
    {
        var grids = data.Truth?.Grids ?? [];
        var frame = BeamCase.FrameOf(grids);
        var region = grids.Count == 0 ? null : new WallRecognizer.Region(
            grids.Min(g => Math.Min(g.X0, g.X1)) - 3000, grids.Min(g => Math.Min(g.Y0, g.Y1)) - 3000,
            grids.Max(g => Math.Max(g.X0, g.X1)) + 3000, grids.Max(g => Math.Max(g.Y0, g.Y1)) + 3000);
        var shapes = FindShapes(data, region, options);
        var loops = data.Hatches.SelectMany(OuterLoops).ToList();
        return Recognize(shapes, frame, data.Texts.Select(t => (t.Text, t.X, t.Y)).ToList(), [], loops, options);
    }

    // 2: centre on a grid in both directions, 1: in one, 0: none (or no grids).
    private static int Grid(Column column, BeamCase.Frame frame, Options options)
    {
        if (frame.AlongU.Count == 0 && frame.AlongV.Count == 0) return 0;
        var (u, v) = (frame.U(column.Cx, column.Cy), frame.V(column.Cx, column.Cy));
        var fromU = column.AngleRadians - frame.ThetaRad;
        var halfU = GridIntersections.HalfExtent(column.Length, column.Width, fromU);
        var halfV = GridIntersections.HalfExtent(column.Length, column.Width, fromU - Math.PI / 2);
        var fitU = GridIntersections.Fit(u, halfU, frame.AlongV, 0, options.GridMarginMm, options.GridToleranceMm);
        var fitV = GridIntersections.Fit(v, halfV, frame.AlongU, 0, options.GridMarginMm, options.GridToleranceMm);
        column.Grid = (fitU.OnGrid, fitV.OnGrid) switch
        {
            (true, true) => GridIntersections.Label(fitU.GridName!, fitV.GridName!),
            (true, false) => $"trục {fitU.GridName}",
            (false, true) => $"trục {fitV.GridName}",
            _ => null,
        };
        return (fitU.OnGrid ? 1 : 0) + (fitV.OnGrid ? 1 : 0);
    }

    // One column per spot: shapes with the same centre and size (outline and hatch, or one layer over another).
    private static List<Column> Merge(IReadOnlyList<Shape> shapes, Options options)
    {
        var columns = new List<Column>();
        var cell = Math.Max(options.SameCenterMm, 1);
        var buckets = new Dictionary<(long, long), List<Column>>();
        // Outlines first, so a column keeps its drawn outline rather than its hatch's.
        foreach (var shape in shapes.OrderBy(shape => shape.IsHatch))
        {
            var (cx, cy) = ((long)Math.Floor(shape.Cx / cell), (long)Math.Floor(shape.Cy / cell));
            Column? same = null;
            for (var dx = -1L; dx <= 1 && same is null; dx++)
                for (var dy = -1L; dy <= 1 && same is null; dy++)
                    if (buckets.TryGetValue((cx + dx, cy + dy), out var near))
                        same = near.FirstOrDefault(column => SameSpot(column, shape, options));
            if (same is null)
            {
                same = new Column
                {
                    Cx = shape.Cx, Cy = shape.Cy, Length = shape.Length, Width = shape.Width, AngleRadians = shape.AngleRadians,
                };
                columns.Add(same);
                if (!buckets.TryGetValue((cx, cy), out var list)) buckets[(cx, cy)] = list = [];
                list.Add(same);
            }
            if (shape.IsHatch) same.HasHatch = true;
            else same.HasOutline = true;
            if (shape.Layer.Length > 0 && !same.Layers.Contains(shape.Layer)) same.Layers.Add(shape.Layer);
        }
        return columns;
    }

    private static bool SameSpot(Column column, Shape shape, Options options) =>
        Math.Sqrt(Math.Pow(column.Cx - shape.Cx, 2) + Math.Pow(column.Cy - shape.Cy, 2)) <= options.SameCenterMm
        && Math.Abs(Math.Min(column.Length, column.Width) - Math.Min(shape.Length, shape.Width)) <= options.SameSizeMm
        && Math.Abs(Math.Max(column.Length, column.Width) - Math.Max(shape.Length, shape.Width)) <= options.SameSizeMm;

    private static bool InSize(Recognition.Shapes.Rectangle rect, Options options) =>
        Math.Min(rect.Length, rect.Width) >= options.MinSideMm && Math.Max(rect.Length, rect.Width) <= options.MaxSideMm;

    // The smallest rectangle around the points (rotating calipers over the convex hull), with its angle in [0, π/2).
    public static Recognition.Shapes.Rectangle? Fit(IReadOnlyList<(double X, double Y)> points)
    {
        var hull = Hull(points);
        if (hull.Count < 3) return null;
        Recognition.Shapes.Rectangle? best = null;
        var bestArea = double.MaxValue;
        for (var i = 0; i < hull.Count; i++)
        {
            var (a, b) = (hull[i], hull[(i + 1) % hull.Count]);
            var angle = Math.Atan2(b.Y - a.Y, b.X - a.X);
            angle -= Math.Floor(angle / (Math.PI / 2)) * (Math.PI / 2);
            var (cos, sin) = (Math.Cos(angle), Math.Sin(angle));
            double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
            foreach (var (x, y) in hull)
            {
                var (u, v) = (x * cos + y * sin, -x * sin + y * cos);
                (minU, maxU, minV, maxV) = (Math.Min(minU, u), Math.Max(maxU, u), Math.Min(minV, v), Math.Max(maxV, v));
            }
            var area = (maxU - minU) * (maxV - minV);
            if (area >= bestArea - 1e-6) continue;
            bestArea = area;
            var (cu, cv) = ((minU + maxU) / 2, (minV + maxV) / 2);
            best = new Recognition.Shapes.Rectangle(cu * cos - cv * sin, cu * sin + cv * cos, maxU - minU, maxV - minV, angle);
        }
        return best;
    }

    private static List<(double X, double Y)> Hull(IReadOnlyList<(double X, double Y)> points)
    {
        var sorted = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (sorted.Count < 3) return sorted;
        static double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) =>
            (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var hull = new List<(double X, double Y)>();
        foreach (var pass in new[] { sorted, Enumerable.Reverse(sorted).ToList() })
        {
            var start = hull.Count;
            foreach (var p in pass)
            {
                while (hull.Count >= start + 2 && Cross(hull[^2], hull[^1], p) <= 1e-9) hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count - 1);
        }
        return hull;
    }

    private static double Area(IReadOnlyList<double[]> loop)
    {
        double sum = 0;
        for (var i = 0; i < loop.Count; i++)
        {
            var (a, b) = (loop[i], loop[(i + 1) % loop.Count]);
            sum += a[0] * b[1] - b[0] * a[1];
        }
        return sum / 2;
    }

    private static double Perimeter(IReadOnlyList<double[]> loop)
    {
        double sum = 0;
        for (var i = 0; i < loop.Count; i++)
        {
            var (a, b) = (loop[i], loop[(i + 1) % loop.Count]);
            sum += Math.Sqrt(Math.Pow(b[0] - a[0], 2) + Math.Pow(b[1] - a[1], 2));
        }
        return Math.Max(sum, 1e-9);
    }

    // Inside the polygon, or within offsetMm of its bounding box when offsetMm > 0 (a column slightly off its cap).
    private static bool Contains(IReadOnlyList<double[]> polygon, double x, double y, double offsetMm)
    {
        if (polygon.Count < 3) return false;
        if (offsetMm > 0)
            return x >= polygon.Min(p => p[0]) - offsetMm && x <= polygon.Max(p => p[0]) + offsetMm
                && y >= polygon.Min(p => p[1]) - offsetMm && y <= polygon.Max(p => p[1]) + offsetMm;
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            if ((polygon[i][1] > y) != (polygon[j][1] > y)
                && x < (polygon[j][0] - polygon[i][0]) * (y - polygon[i][1]) / (polygon[j][1] - polygon[i][1]) + polygon[i][0])
                inside = !inside;
        return inside;
    }

    // "COT", "CỘT", "COLUMN", "01 Colum (cột)", "S-COL".
    [GeneratedRegex(@"(?<![a-zA-Z])(c[oộ]t|colum\w*|col)(?![a-zA-Z])", RegexOptions.IgnoreCase)]
    private static partial Regex ColumnLayer();

    [GeneratedRegex(@"c[uử]a|door|window|furn|n[oộ]i\s?th[aấ]t|thi[eế]t\s?b[iị]|tbvs|(?<![a-z])wc(?![a-z])|dim|text|anno|title|khung\s?t[eê]n|logo", RegexOptions.IgnoreCase)]
    private static partial Regex OtherLayer();
}
