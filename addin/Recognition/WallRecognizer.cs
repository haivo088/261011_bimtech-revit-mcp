using System.Text.RegularExpressions;

namespace Bimtech.RevitMcp.Recognition;

// Structural walls from hatches, read the way an engineer reads a plan. Hatches are where walls come from (concrete
// is hatched); whether a hatched strip really is a structural wall is weighed from evidence, each item with its
// reason so the review window can say why:
//   + hatch pattern shared with the columns, thickness of a concrete wall, on a grid, joined to a column, on top of a
//     wall of the storey below, part of a cluster of walls (a core), lift text inside that core, a wall-like layer name;
//   − thickness of a masonry wall, short for its thickness (a flat column), layer off in the DWG, a column or masonry
//     layer name.
// A hatch no longer than a column (ColumnMaxLengthMm) is a column, unless it lines up with a wall of its thickness a
// door away (a wall cut by doors). Layer names are a hint, never a gate. Weighed on the
// Evidence scale: a hatched strip starts at 50% (the hatch is why it is a candidate), a wall below +20%, other relations
// ±10%, layer names ±5%, masonry thickness −20%, layer off −30%; sure from 50%, to review from 20%, dropped below,
// still reported with its reasons.
//
// Geometry: each hatch's outer loop is cut into thin strips along the grid frame (or its own longest edge), so an
// L, T or U shaped core hatch gives one wall per leg; strips of the same thickness on one line are merged (repeated
// or split hatches), then wall ends that stop on another wall's face are moved to its centre line, as Revit joins
// walls. All lengths are model mm.
public static partial class WallRecognizer
{
    public sealed class Options
    {
        public double MinThicknessMm { get; init; } = 100;
        public double MaxThicknessMm { get; init; } = 600;
        // Below this length/thickness a strip is a column or a blob, not a wall.
        public double MinElongation { get; init; } = 2.5;
        // Below this, a wall could still be a flat column.
        public double WallElongation { get; init; } = 4;
        // A hatch whose longer side is at most this is a column (a flat 300 x 800 one included), not a wall.
        public double ColumnMaxLengthMm { get; init; } = 1000;
        // A short piece in line with a wall of its thickness, at most this far from its end, is that wall cut by a door.
        public double MaxDoorMm { get; init; } = 2500;
        public double GridToleranceMm { get; init; } = 15;
        public double JoinGapMm { get; init; } = 10;
        public double ColumnContactMm { get; init; } = 50;
        public double BelowOffsetMm { get; init; } = 50;
    }

    public sealed record Region(double MinX, double MinY, double MaxX, double MaxY)
    {
        public bool Contains(double x, double y) => x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;
    }

    public sealed class Wall : Weighed
    {
        public double X0 { get; set; }
        public double Y0 { get; set; }
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double ThicknessMm { get; init; }
        public string Layer { get; init; } = "";
        public string? Pattern { get; init; }
        public bool LayerOff { get; init; }
        public string? Grid { get; set; }

        public double Length => Math.Sqrt((X1 - X0) * (X1 - X0) + (Y1 - Y0) * (Y1 - Y0));
        internal (double X, double Y) Direction => ((X1 - X0) / Length, (Y1 - Y0) / Length);
    }

    // ColumnPatternsFromModel: the column patterns were taken from hatches inside modeled columns (else from shape only).
    public sealed record Result(List<Wall> Walls, List<Wall> Dropped, int HatchesInRegion, Dictionary<string, int> Skipped,
        List<string> ColumnPatterns, bool ColumnPatternsFromModel);

    // Evidence, worded without names so the review window can count them over a group.
    public const string HatchReason = "có hatch";
    public const string ColumnPatternReason = "cùng kiểu hatch với cột";
    public const string ConcreteSizeReason = "dày đúng cỡ vách bê tông";
    public const string MasonrySizeReason = "dày cỡ tường xây";
    public const string ThinReason = "mỏng hơn 150 mm";
    public const string ShortReason = "ngắn so với bề dày, có thể là cột dẹt";
    public const string GridReason = "nằm trên trục";
    public const string ColumnReason = "nối vào cột";
    public const string BelowReason = "trùng vách tầng dưới";
    public const string CoreReason = "thuộc cụm vách (lõi)";
    public const string LiftTextReason = "có chữ thang máy trong lõi";
    public const string LayerOffReason = "layer đang tắt trong DWG";
    public const string WallLayerReason = "tên layer giống vách";
    public const string OtherLayerReason = "tên layer giống cột hoặc tường xây";
    public const string UnsupportedReason = "không nối cột, không có vách tầng dưới";
    public const string ConcretePatternReason = "hatch kiểu bê tông";
    public const string FoundationReason = "nằm trên đài móng";
    public const string NoFoundationReason = "không nằm trên đài móng hay vách tầng dưới";

    private static readonly double[] MasonrySizes = [100, 105, 110, 115, 120, 210, 220, 230];

    public static Result Recognize(CadCase scene, BeamCase.Frame frame, IReadOnlyList<CaseSupport> columns,
        IReadOnlyList<CaseSupport> wallsBelow, Region? region, Options? options = null)
    {
        options ??= new Options();
        var skipped = new Dictionary<string, int>();
        void Skip(string reason) => skipped[reason] = skipped.GetValueOrDefault(reason) + 1;

        // Every separate area of a hatch on its own, with the holes inside it: one hatch often fills a whole core in
        // several pieces (the walls cut by doors, an H-shaped part beside them).
        var hatches = scene.Hatches
            .SelectMany(hatch => ColumnRecognizer.OuterLoops(hatch).Select(outer => (Hatch: hatch, Outer: outer,
                Loops: hatch.Loops.Where(loop => ReferenceEquals(loop, outer) || (loop.Count >= 3
                    && Math.Abs(Area(loop)) < Math.Abs(Area(outer)) && Inside(outer, loop[0][0], loop[0][1]))).ToList())))
            .Where(h => region is null || region.Contains(h.Outer.Average(p => p[0]), h.Outer.Average(p => p[1])))
            .ToList();

        var (columnPatterns, fromModel) = ColumnPatterns(hatches.Select(h => (h.Hatch.Pattern, h.Outer)).ToList(), columns);

        var strips = new List<Wall>();
        var shortPieces = new List<(CadHatch Hatch, List<double[]> Outer, List<List<double[]>> Loops, CadStyle? Style)>();
        foreach (var (hatch, outer, loops) in hatches)
        {
            var style = hatch.Style >= 0 && hatch.Style < scene.Styles.Count ? scene.Styles[hatch.Style] : null;
            if (BoxSize(outer, LongestEdgeAngle(outer)).Length <= options.ColumnMaxLengthMm)
            {
                // Kept aside: a short piece in line with a wall, a door away, is that wall cut by the door.
                shortPieces.Add((hatch, outer, loops, style));
                Skip(ColumnSizedSkip(options));
                continue;
            }
            var found = Strips(outer, loops, frame.ThetaRad, options) ?? Strips(outer, loops, LongestEdgeAngle(outer), options);
            if (found is null)
            {
                Skip("hatch không phải ghép từ hình chữ nhật (cong, xiên)");
                continue;
            }
            if (found.Count == 0)
            {
                Skip("hatch không có dải mảnh nào (cột, đài, vùng lớn)");
                continue;
            }
            foreach (var (x0, y0, x1, y1, thickness) in found)
                strips.Add(new Wall
                {
                    X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, ThicknessMm = Math.Round(thickness / 5) * 5,
                    Layer = style?.Layer ?? "", Pattern = hatch.Pattern, LayerOff = style?.Off == true,
                });
        }

        var walls = Merge(strips, options);
        Join(walls, options);
        walls = walls.Where(wall => wall.Length >= 2 * wall.ThicknessMm).ToList();
        walls.AddRange(PiecesInLine(shortPieces, walls, frame, options, skipped, ColumnSizedSkip(options)));
        // A strip that is a modeled column (a long column's own hatch) is that column, not a wall.
        var modelColumns = columns.Where(c => c.Kind == "column").ToList();
        walls = walls.Where(wall =>
        {
            if (!modelColumns.Any(column => Inside(column.Points, (wall.X0 + wall.X1) / 2, (wall.Y0 + wall.Y1) / 2))) return true;
            Skip("dải hatch nằm trong cột đã dựng");
            return false;
        }).ToList();

        foreach (var wall in walls) Score(wall, frame, columns, wallsBelow, columnPatterns, fromModel, options);
        ScoreCores(walls, scene.Texts, options);
        return new Result(walls.Where(wall => Evidence.IsKept(wall.Percent)).ToList(), walls.Where(wall => !Evidence.IsKept(wall.Percent)).ToList(),
            hatches.Count, skipped, columnPatterns, fromModel);
    }

    // Patterns of hatches shaped like columns: inside a modeled column when there are some, else squarish and column-sized.
    private static (List<string> Patterns, bool FromModel) ColumnPatterns(
        List<(string? Pattern, List<double[]> Outer)> hatches, IReadOnlyList<CaseSupport> columns)
    {
        var modelColumns = columns.Where(support => support.Kind == "column").ToList();
        bool Squarish(List<double[]> outer)
        {
            var (length, width) = BoxSize(outer, LongestEdgeAngle(outer));
            return width >= 150 && length <= 2000 && length / width < 2;
        }
        var inColumns = hatches.Where(h => modelColumns.Any(column =>
                Inside(column.Points, h.Outer.Average(p => p[0]), h.Outer.Average(p => p[1]))))
            .ToList();
        var source = inColumns.Count > 0 ? inColumns : hatches.Where(h => Squarish(h.Outer)).ToList();
        return (source.GroupBy(h => h.Pattern ?? "").Where(g => g.Count() >= 2).Select(g => g.Key).ToList(), inColumns.Count > 0);
    }

    // Thin rectangular strips of a hatch whose edges all run along the frame at angle theta; null when they do not.
    private static List<(double X0, double Y0, double X1, double Y1, double Thickness)>? Strips(
        List<double[]> outer, List<List<double[]>> loops, double theta, Options options)
    {
        var (cos, sin) = (Math.Cos(theta), Math.Sin(theta));
        (double U, double V) Local(double[] p) => (p[0] * cos + p[1] * sin, -p[0] * sin + p[1] * cos);
        (double X, double Y) Model(double u, double v) => (u * cos - v * sin, u * sin + v * cos);

        var polygon = Simplify(outer.Select(Local).ToList());
        if (polygon.Count < 4) return null;
        const double tilt = 0.0087; // sin 0.5°
        for (var i = 0; i < polygon.Count; i++)
        {
            var (a, b) = (polygon[i], polygon[(i + 1) % polygon.Count]);
            var (du, dv) = (Math.Abs(b.U - a.U), Math.Abs(b.V - a.V));
            var length = Math.Sqrt(du * du + dv * dv);
            if (dv > tilt * length && du > tilt * length) return null;
        }

        var us = Levels(polygon.Select(p => p.U));
        var vs = Levels(polygon.Select(p => p.V));
        if (us.Count > 60 || vs.Count > 60) return null;
        var localLoops = loops.Select(loop => loop.Select(Local).ToList()).ToList();
        var inside = new bool[us.Count - 1, vs.Count - 1];
        for (var i = 0; i < us.Count - 1; i++)
            for (var j = 0; j < vs.Count - 1; j++)
            {
                var (cu, cv) = ((us[i] + us[i + 1]) / 2, (vs[j] + vs[j + 1]) / 2);
                inside[i, j] = localLoops.Count(loop => Inside(loop, cu, cv)) % 2 == 1;
            }

        var result = new List<(double, double, double, double, double)>();
        void Add(double u0, double u1, double v0, double v1, bool alongU)
        {
            var (length, thickness) = alongU ? (u1 - u0, v1 - v0) : (v1 - v0, u1 - u0);
            if (thickness < options.MinThicknessMm - 2 || thickness > options.MaxThicknessMm + 2) return;
            if (length < options.MinElongation * thickness) return;
            var (a, b) = alongU ? (Model(u0, (v0 + v1) / 2), Model(u1, (v0 + v1) / 2)) : (Model((u0 + u1) / 2, v0), Model((u0 + u1) / 2, v1));
            result.Add((Math.Round(a.X, 2), Math.Round(a.Y, 2), Math.Round(b.X, 2), Math.Round(b.Y, 2), thickness));
        }
        // How much of a band's long side is open (not hatched): a wall has open space on both long sides, a band
        // inside a larger hatched area (a floor zone) has hatch on one side or both.
        double OpenU(int first, int last, int j)
        {
            if (j < 0 || j >= vs.Count - 1) return 1;
            double open = 0;
            for (var i = first; i <= last; i++) if (!inside[i, j]) open += us[i + 1] - us[i];
            return open / (us[last + 1] - us[first]);
        }
        double OpenV(int first, int last, int i)
        {
            if (i < 0 || i >= us.Count - 1) return 1;
            double open = 0;
            for (var j = first; j <= last; j++) if (!inside[i, j]) open += vs[j + 1] - vs[j];
            return open / (vs[last + 1] - vs[first]);
        }
        const double openSides = 0.5;
        // Runs of inside cells along each row; rows with the very same run are one strip (a split thickness).
        foreach (var (first, last, from, to) in Runs(us.Count - 1, vs.Count - 1, (i, j) => inside[i, j]))
            if (OpenU(first, last, from - 1) >= openSides && OpenU(first, last, to + 1) >= openSides)
                Add(us[first], us[last + 1], vs[from], vs[to + 1], alongU: true);
        foreach (var (first, last, from, to) in Runs(vs.Count - 1, us.Count - 1, (j, i) => inside[i, j]))
            if (OpenV(first, last, from - 1) >= openSides && OpenV(first, last, to + 1) >= openSides)
                Add(us[from], us[to + 1], vs[first], vs[last + 1], alongU: false);
        return result;
    }

    // Runs along the first index, merged across consecutive second indices that have the same run.
    private static List<(int First, int Last, int From, int To)> Runs(int along, int across, Func<int, int, bool> inside)
    {
        var done = new List<(int, int, int, int)>();
        var open = new Dictionary<(int, int), int>();
        for (var j = 0; j <= across; j++)
        {
            var runs = new HashSet<(int, int)>();
            if (j < across)
                for (var i = 0; i < along; i++)
                {
                    if (!inside(i, j)) continue;
                    var start = i;
                    while (i + 1 < along && inside(i + 1, j)) i++;
                    runs.Add((start, i));
                }
            foreach (var key in open.Keys.Where(key => !runs.Contains(key)).ToList())
            {
                done.Add((key.Item1, key.Item2, open[key], j - 1));
                open.Remove(key);
            }
            foreach (var run in runs) open.TryAdd(run, j);
        }
        return done;
    }

    // Strips of the same thickness on one centre line that overlap or nearly touch become one wall.
    private static List<Wall> Merge(List<Wall> strips, Options options)
    {
        var result = new List<Wall>();
        foreach (var strip in strips.OrderByDescending(s => s.Length))
        {
            var (dx, dy) = strip.Direction;
            var into = result.FirstOrDefault(wall =>
            {
                if (Math.Abs(wall.ThicknessMm - strip.ThicknessMm) > 5) return false;
                var (wx, wy) = wall.Direction;
                if (Math.Abs(wx * dy - wy * dx) > 0.017) return false;
                if (Math.Abs(Across(wall, strip.X0, strip.Y0)) > 5 || Math.Abs(Across(wall, strip.X1, strip.Y1)) > 5) return false;
                var (a, b) = (Along(wall, strip.X0, strip.Y0), Along(wall, strip.X1, strip.Y1));
                return Math.Max(a, b) >= -options.JoinGapMm && Math.Min(a, b) <= wall.Length + options.JoinGapMm;
            });
            if (into is null)
            {
                result.Add(strip);
                continue;
            }
            var (wdx, wdy) = into.Direction;
            var (ox, oy) = (into.X0, into.Y0);
            var along = new[] { 0, into.Length, Along(into, strip.X0, strip.Y0), Along(into, strip.X1, strip.Y1) };
            var (min, max) = (along.Min(), along.Max());
            (into.X0, into.Y0, into.X1, into.Y1) = (ox + wdx * min, oy + wdy * min, ox + wdx * max, oy + wdy * max);
        }
        return result;
    }

    // A wall end that stops on (or just inside) the face of a perpendicular wall moves to that wall's centre line,
    // so corners and T-junctions meet as Revit joins walls.
    private static void Join(List<Wall> walls, Options options)
    {
        foreach (var wall in walls)
            for (var end = 0; end < 2; end++)
            {
                var (ex, ey) = end == 0 ? (wall.X0, wall.Y0) : (wall.X1, wall.Y1);
                var (dx, dy) = wall.Direction;
                foreach (var other in walls)
                {
                    if (ReferenceEquals(other, wall)) continue;
                    var (ox, oy) = other.Direction;
                    var dot = dx * ox + dy * oy;
                    if (Math.Abs(dot) > 0.035) continue;
                    var across = Across(other, ex, ey);
                    var reach = other.ThicknessMm / 2 + options.JoinGapMm;
                    if (Math.Abs(across) > reach) continue;
                    var along = Along(other, ex, ey);
                    if (along < -wall.ThicknessMm / 2 - options.JoinGapMm || along > other.Length + wall.ThicknessMm / 2 + options.JoinGapMm) continue;
                    // Move along this wall's own direction until the end is on the other's centre line.
                    var (nx, ny) = (-oy, ox);
                    var speed = dx * nx + dy * ny;
                    if (Math.Abs(speed) < 0.5) continue;
                    var step = -across / speed;
                    var (x, y) = (Math.Round(ex + dx * step, 2), Math.Round(ey + dy * step, 2));
                    if (end == 0) (wall.X0, wall.Y0) = (x, y);
                    else (wall.X1, wall.Y1) = (x, y);
                    break;
                }
            }
    }

    private static void Score(Wall wall, BeamCase.Frame frame, IReadOnlyList<CaseSupport> columns,
        IReadOnlyList<CaseSupport> wallsBelow, List<string> columnPatterns, bool fromModel, Options options)
    {
        void Add(int percent, string reason) => wall.Add(percent, reason);
        // Patterns read off the modeled columns are sure; guessed from squarish hatches, only a hint.
        if (columnPatterns.Contains(wall.Pattern ?? "")) Add(fromModel ? 20 : 10, ColumnPatternReason);
        if (WallLayer().IsMatch(wall.Layer)) Add(5, WallLayerReason);
        else if (OtherLayer().IsMatch(wall.Layer)) Add(-5, OtherLayerReason);

        var t = wall.ThicknessMm;
        // A concrete hatch says concrete whatever the thickness: a 220 concrete wall is not masonry.
        var concrete = ConcretePattern().IsMatch(wall.Pattern ?? "");
        if (concrete) Add(10, ConcretePatternReason);
        if (MasonrySizes.Any(size => Math.Abs(size - t) < 3)) { if (!concrete) Add(-20, MasonrySizeReason); }
        else if (t < 150) Add(-10, ThinReason);
        else if (t <= 500 && Math.Abs(t / 50 - Math.Round(t / 50)) < 0.01) Add(10, ConcreteSizeReason);

        if (wall.Length < options.WallElongation * t) Add(-10, ShortReason);
        if (wall.LayerOff) Add(-30, LayerOffReason);

        if (OnGrid(wall, frame, options) is { } grid)
        {
            wall.Grid = grid;
            Add(10, GridReason);
        }
        var touchesColumn = columns.Where(c => c.Kind == "column").Any(column =>
            new[] { (wall.X0, wall.Y0), (wall.X1, wall.Y1) }.Any(end =>
                Inside(column.Points, end.Item1, end.Item2) || Distance(column.Points, end.Item1, end.Item2) <= options.ColumnContactMm + t / 2));
        if (touchesColumn) Add(10, ColumnReason);

        var (dx, dy) = wall.Direction;
        var onWallBelow = wallsBelow.Where(s => s.Kind == "wall" && s.Points.Count == 4).Any(below =>
        {
            var (bx0, by0) = ((below.Points[0][0] + below.Points[3][0]) / 2, (below.Points[0][1] + below.Points[3][1]) / 2);
            var (bx1, by1) = ((below.Points[1][0] + below.Points[2][0]) / 2, (below.Points[1][1] + below.Points[2][1]) / 2);
            var length = Math.Sqrt((bx1 - bx0) * (bx1 - bx0) + (by1 - by0) * (by1 - by0));
            if (length < 1) return false;
            var (ux, uy) = ((bx1 - bx0) / length, (by1 - by0) / length);
            if (Math.Abs(ux * dy - uy * dx) > 0.035) return false;
            double AcrossBelow(double x, double y) => (x - bx0) * -uy + (y - by0) * ux;
            if (Math.Abs(AcrossBelow(wall.X0, wall.Y0)) > options.BelowOffsetMm || Math.Abs(AcrossBelow(wall.X1, wall.Y1)) > options.BelowOffsetMm) return false;
            double AlongBelow(double x, double y) => (x - bx0) * ux + (y - by0) * uy;
            var (a, b) = (AlongBelow(wall.X0, wall.Y0), AlongBelow(wall.X1, wall.Y1));
            var overlap = Math.Min(Math.Max(a, b), length) - Math.Max(Math.Min(a, b), 0);
            return overlap >= 0.5 * wall.Length;
        });
        if (onWallBelow) Add(20, BelowReason);
        // A structural wall goes down to the foundation: over a pile cap (at any depth), or on the wall below.
        var foundations = wallsBelow.Where(s => s.Kind == "cap").ToList();
        var onFoundation = foundations.Count > 0 && OnFoundation(wall, foundations);
        if (onFoundation) Add(20, FoundationReason);
        if (foundations.Count > 0)
        {
            // Foundations are modeled, so a wall over none and on no wall is not carried: always reviewed.
            if (!onWallBelow && !onFoundation) wall.Veto(NoFoundationReason);
        }
        else if (!touchesColumn && !onWallBelow) Add(-20, UnsupportedReason);
    }

    private static string ColumnSizedSkip(Options options) => "hatch cỡ cột (cạnh dài ≤ " + options.ColumnMaxLengthMm.ToString("0") + " mm)";

    // Column-sized pieces that continue a wall: same thickness, on its centre line, a door's width or less from it.
    private static List<Wall> PiecesInLine(
        List<(CadHatch Hatch, List<double[]> Outer, List<List<double[]>> Loops, CadStyle? Style)> pieces, List<Wall> walls,
        BeamCase.Frame frame, Options options, Dictionary<string, int> skipped, string skipReason)
    {
        var found = new List<Wall>();
        foreach (var (hatch, outer, loops, style) in pieces)
        {
            var strips = Strips(outer, loops, frame.ThetaRad, options) ?? Strips(outer, loops, LongestEdgeAngle(outer), options) ?? [];
            var added = false;
            foreach (var (x0, y0, x1, y1, thickness) in strips)
            {
                var piece = new Wall
                {
                    X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, ThicknessMm = Math.Round(thickness / 5) * 5,
                    Layer = style?.Layer ?? "", Pattern = hatch.Pattern, LayerOff = style?.Off == true,
                };
                var (dx, dy) = piece.Direction;
                // Gap along the wall's centre line from the wall to the piece; null when not on that line.
                double? Gap(Wall wall)
                {
                    if (Math.Abs(wall.ThicknessMm - piece.ThicknessMm) > 5) return null;
                    var (wx, wy) = wall.Direction;
                    if (Math.Abs(wx * dy - wy * dx) > 0.017) return null;
                    if (Math.Abs(Across(wall, piece.X0, piece.Y0)) > 5 || Math.Abs(Across(wall, piece.X1, piece.Y1)) > 5) return null;
                    var (a, b) = (Along(wall, piece.X0, piece.Y0), Along(wall, piece.X1, piece.Y1));
                    return Math.Max(Math.Min(a, b) - wall.Length, -Math.Max(a, b));
                }
                if (!walls.Any(wall => Gap(wall) is >= 0 and var gap && gap <= options.MaxDoorMm)) continue;
                added = true;
                // The same piece drawn twice (two identical hatches) is placed once.
                if (found.Concat(walls).Any(other => Gap(other) is < 0)) continue;
                found.Add(piece);
            }
            if (added) skipped[skipReason]--;
        }
        foreach (var key in skipped.Where(pair => pair.Value <= 0).Select(pair => pair.Key).ToList()) skipped.Remove(key);
        return found;
    }

    // At least 80% of the centre line lies over the caps' outlines.
    private static bool OnFoundation(Wall wall, IReadOnlyList<CaseSupport> foundations)
    {
        const int samples = 20;
        var over = 0;
        for (var i = 0; i <= samples; i++)
        {
            var (x, y) = (wall.X0 + (wall.X1 - wall.X0) * i / samples, wall.Y0 + (wall.Y1 - wall.Y0) * i / samples);
            if (foundations.Any(cap => Inside(cap.Points, x, y))) over++;
        }
        return over >= 0.8 * (samples + 1);
    }

    // Walls joined end to face form clusters; a cluster of three or more walls in both directions is a core, and
    // lift text inside it says what the core is for.
    private static void ScoreCores(List<Wall> walls, IReadOnlyList<CadText> texts, Options options)
    {
        var parent = Enumerable.Range(0, walls.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        for (var i = 0; i < walls.Count; i++)
            for (var j = i + 1; j < walls.Count; j++)
                if (Touch(walls[i], walls[j], options) || Touch(walls[j], walls[i], options))
                    parent[Find(i)] = Find(j);

        foreach (var cluster in Enumerable.Range(0, walls.Count).GroupBy(Find).Select(g => g.Select(i => walls[i]).ToList()))
        {
            if (cluster.Count < 3) continue;
            var (dx, dy) = cluster[0].Direction;
            if (!cluster.Any(wall => Math.Abs(wall.Direction.X * dx + wall.Direction.Y * dy) < 0.035)) continue;
            foreach (var wall in cluster) wall.Add(10, CoreReason);
            var (minX, maxX) = (cluster.Min(w => Math.Min(w.X0, w.X1)), cluster.Max(w => Math.Max(w.X0, w.X1)));
            var (minY, maxY) = (cluster.Min(w => Math.Min(w.Y0, w.Y1)), cluster.Max(w => Math.Max(w.Y0, w.Y1)));
            if (texts.Any(text => text.X >= minX && text.X <= maxX && text.Y >= minY && text.Y <= maxY && LiftText().IsMatch(text.Text)))
                foreach (var wall in cluster) wall.Add(10, LiftTextReason);
        }
    }

    // An end of a lies on b (within b's half thickness plus a's and the gap).
    private static bool Touch(Wall a, Wall b, Options options) =>
        new[] { (a.X0, a.Y0), (a.X1, a.Y1) }.Any(end =>
        {
            var along = Along(b, end.Item1, end.Item2);
            var slack = a.ThicknessMm / 2 + options.JoinGapMm;
            return Math.Abs(Across(b, end.Item1, end.Item2)) <= b.ThicknessMm / 2 + slack && along >= -slack && along <= b.Length + slack;
        });

    // The grid the wall's centre line or one of its faces lies on, if any.
    private static string? OnGrid(Wall wall, BeamCase.Frame frame, Options options)
    {
        var (du, dv) = (frame.U(wall.X1, wall.Y1) - frame.U(wall.X0, wall.Y0), frame.V(wall.X1, wall.Y1) - frame.V(wall.X0, wall.Y0));
        var alongU = Math.Abs(dv) <= 0.035 * wall.Length;
        if (!alongU && Math.Abs(du) > 0.035 * wall.Length) return null;
        var (mx, my) = ((wall.X0 + wall.X1) / 2, (wall.Y0 + wall.Y1) / 2);
        var position = alongU ? frame.V(mx, my) : frame.U(mx, my);
        var axes = alongU ? frame.AlongU : frame.AlongV;
        var half = wall.ThicknessMm / 2;
        return axes.FirstOrDefault(axis => new[] { position, position - half, position + half }
            .Any(p => Math.Abs(axis.PositionMm - p) <= options.GridToleranceMm))?.Name;
    }

    private static double Along(Wall wall, double x, double y)
    {
        var (dx, dy) = wall.Direction;
        return (x - wall.X0) * dx + (y - wall.Y0) * dy;
    }

    private static double Across(Wall wall, double x, double y)
    {
        var (dx, dy) = wall.Direction;
        return (x - wall.X0) * -dy + (y - wall.Y0) * dx;
    }

    // Distinct values within 2 mm, sorted.
    private static List<double> Levels(IEnumerable<double> values)
    {
        var levels = new List<double>();
        foreach (var value in values.OrderBy(v => v))
            if (levels.Count == 0 || value - levels[^1] > 2) levels.Add(value);
        return levels;
    }

    // Without repeated points and points on a straight run (within 1 mm).
    private static List<(double U, double V)> Simplify(List<(double U, double V)> points)
    {
        var list = points.ToList();
        var changed = true;
        while (changed && list.Count >= 3)
        {
            changed = false;
            for (var i = 0; i < list.Count && list.Count >= 3; i++)
            {
                var (a, p, b) = (list[(i + list.Count - 1) % list.Count], list[i], list[(i + 1) % list.Count]);
                var (abu, abv) = (b.U - a.U, b.V - a.V);
                var length = Math.Sqrt(abu * abu + abv * abv);
                var offLine = length < 1e-9
                    ? Math.Sqrt((p.U - a.U) * (p.U - a.U) + (p.V - a.V) * (p.V - a.V))
                    : Math.Abs((p.U - a.U) * abv - (p.V - a.V) * abu) / length;
                var between = length < 1e-9 || ((p.U - a.U) * abu + (p.V - a.V) * abv) / length is var t && t >= -1 && t <= length + 1;
                if (offLine <= 1 && between)
                {
                    list.RemoveAt(i);
                    changed = true;
                    i--;
                }
            }
        }
        return list;
    }

    private static double LongestEdgeAngle(List<double[]> loop)
    {
        var (best, angle) = (0.0, 0.0);
        for (var i = 0; i < loop.Count; i++)
        {
            var (a, b) = (loop[i], loop[(i + 1) % loop.Count]);
            var length = Math.Sqrt((b[0] - a[0]) * (b[0] - a[0]) + (b[1] - a[1]) * (b[1] - a[1]));
            if (length > best) (best, angle) = (length, Math.Atan2(b[1] - a[1], b[0] - a[0]));
        }
        return angle;
    }

    // Size of the loop's box in the frame at theta: (longer side, shorter side).
    private static (double Length, double Width) BoxSize(List<double[]> loop, double theta)
    {
        var (cos, sin) = (Math.Cos(theta), Math.Sin(theta));
        var us = loop.Select(p => p[0] * cos + p[1] * sin).ToList();
        var vs = loop.Select(p => -p[0] * sin + p[1] * cos).ToList();
        var (a, b) = (us.Max() - us.Min(), vs.Max() - vs.Min());
        return (Math.Max(a, b), Math.Min(a, b));
    }

    private static double Area(List<double[]> loop)
    {
        double sum = 0;
        for (var i = 0; i < loop.Count; i++)
        {
            var (a, b) = (loop[i], loop[(i + 1) % loop.Count]);
            sum += a[0] * b[1] - b[0] * a[1];
        }
        return sum / 2;
    }

    private static bool Inside(IReadOnlyList<double[]> polygon, double x, double y) =>
        Inside(polygon.Select(p => (p[0], p[1])).ToList(), x, y);

    private static bool Inside(List<(double U, double V)> polygon, double x, double y)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var (a, b) = (polygon[i], polygon[j]);
            if ((a.V > y) != (b.V > y) && x < (b.U - a.U) * (y - a.V) / (b.V - a.V) + a.U) inside = !inside;
        }
        return inside;
    }

    private static double Distance(IReadOnlyList<double[]> polygon, double x, double y)
    {
        var best = double.MaxValue;
        for (var i = 0; i < polygon.Count; i++)
        {
            var (a, b) = (polygon[i], polygon[(i + 1) % polygon.Count]);
            var (abx, aby) = (b[0] - a[0], b[1] - a[1]);
            var lengthSquared = abx * abx + aby * aby;
            var t = lengthSquared < 1e-9 ? 0 : Math.Clamp(((x - a[0]) * abx + (y - a[1]) * aby) / lengthSquared, 0, 1);
            var (px, py) = (a[0] + abx * t - x, a[1] + aby * t - y);
            best = Math.Min(best, Math.Sqrt(px * px + py * py));
        }
        return best;
    }

    [GeneratedRegex(@"THANG\s*M[ÁA]Y|\bLIFT\b|\bELEV|\bTM\d*\b", RegexOptions.IgnoreCase)]
    private static partial Regex LiftText();

    [GeneratedRegex(@"V[AÁ]CH|WALL|CORE|L[OÕ]I", RegexOptions.IgnoreCase)]
    private static partial Regex WallLayer();

    // Concrete hatch patterns: BETONG, AR-CONC, CONCRETE, BTCT, "bê tông".
    [GeneratedRegex(@"b[eê]\s?t[oô]ng|conc|btct", RegexOptions.IgnoreCase)]
    private static partial Regex ConcretePattern();

    [GeneratedRegex(@"C[OỘ]T|COL|T[UƯ][OỜ]NG|G[AẠ]CH", RegexOptions.IgnoreCase)]
    private static partial Regex OtherLayer();
}
