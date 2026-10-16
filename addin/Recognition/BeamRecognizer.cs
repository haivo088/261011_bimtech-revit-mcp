namespace Bimtech.RevitMcp.Recognition;

// Main beams from a CAD plan, found from the columns already in the model ("column first, grids as the ruler").
// Everything is in the grid's own frame (u, v in mm), so beams along the grids run along u or v.
//   1. CAD lines within 2° of u or v are kept (others counted as skewed); lines lying inside a column or a wall
//      (outlines, hatch) are dropped.
//   2. At each face of each column, a pair of parallel lines leaving the face (within probeMm), 100-1000 mm apart,
//      with its middle inside the column's width, starts a beam. A line exactly midway between two others is a
//      centre line, not an edge.
//   3. Both edges are followed away from the column, across gaps up to maxGapMm (where another beam crosses), to the
//      first column or wall in the beam's band. The beam runs centre to centre of the two supports; it splits only at
//      supports, not at grid crossings. Edges that stop before any support are reported as dangling (secondary beam
//      or console, handled later).
//   4. Marks ("D1 (220x600)") go to the nearest beam parallel to the text within tagDistanceMm, then spread to the
//      beams continuing it in line (same width, through a shared support) until another mark.
public static class BeamRecognizer
{
    public sealed record Line(double U0, double V0, double U1, double V1);

    // A column or wall in the model: its extent in the frame and its centre. Label: "A-1", a mark, or a position.
    public sealed record Support(string Label, double MinU, double MaxU, double MinV, double MaxV, double CenterU, double CenterV, bool IsColumn);

    // A text in the view. AngleRad: direction of the text in the frame (null when unknown).
    public sealed record Text(string Content, double U, double V, double? AngleRad);

    public sealed record Options
    {
        public double MinWidthMm { get; init; } = 100;
        public double MaxWidthMm { get; init; } = 1000;
        public double ProbeMm { get; init; } = 300;
        public double MaxGapMm { get; init; } = 1500;
        public double MinCoverage { get; init; } = 0.6;
        public double AngleToleranceDeg { get; init; } = 2;
        public double EdgeToleranceMm { get; init; } = 5;
        public double TagDistanceMm { get; init; } = 1000;
        public double MinDanglingMm { get; init; } = 500;
    }

    // A beam from one support to the next. AlongU: runs along u at v = Cross; else along v at u = Cross.
    // Start/End: the supports' centres along the beam. Coverage: share of the span drawn by the two edges.
    public sealed record Beam(
        Support From, Support To, bool AlongU, double Cross, double Start, double End, double WidthMm, double Coverage,
        BeamTag? Tag, bool TagInherited);

    // Edges leaving a column that reach no support. Length: how far they were followed.
    public sealed record Dangling(Support From, bool AlongU, double Cross, double LengthMm);

    public sealed record Result(
        List<Beam> Beams, List<Dangling> Dangling, int SkewedLines, int MaskedLines, List<BeamTag> UnusedTags);

    public static Result Recognize(
        IReadOnlyList<Line> lines, IReadOnlyList<Support> supports, IReadOnlyList<Text> texts, Options? options = null)
    {
        options ??= new Options();
        var tolerance = Math.Sin(options.AngleToleranceDeg * Math.PI / 180);
        var alongU = new List<Seg>();
        var alongV = new List<Seg>();
        int skewed = 0, masked = 0;
        foreach (var line in lines)
        {
            var (du, dv) = (line.U1 - line.U0, line.V1 - line.V0);
            var length = Math.Sqrt(du * du + dv * dv);
            if (length < 20) continue;
            if (Inside(line, supports, options.EdgeToleranceMm * 2))
            {
                masked++;
                continue;
            }
            if (Math.Abs(dv) / length <= tolerance)
                alongU.Add(new Seg((line.V0 + line.V1) / 2, Math.Min(line.U0, line.U1), Math.Max(line.U0, line.U1)));
            else if (Math.Abs(du) / length <= tolerance)
                alongV.Add(new Seg((line.U0 + line.U1) / 2, Math.Min(line.V0, line.V1), Math.Max(line.V0, line.V1)));
            else
                skewed++;
        }

        var boxesU = supports.Select(s => new Box(s.MinU, s.MaxU, s.MinV, s.MaxV, s.CenterU, s)).ToList();
        var boxesV = supports.Select(s => new Box(s.MinV, s.MaxV, s.MinU, s.MaxU, s.CenterV, s)).ToList();
        var dangling = new List<Dangling>();
        var beams = Along(alongU, boxesU, true, options, dangling).Concat(Along(alongV, boxesV, false, options, dangling)).ToList();

        var unused = AssignTags(beams, texts, options);
        Spread(beams, options);
        return new Result(beams, dangling, skewed, masked, unused);
    }

    // A line in a beam's direction: Y across it, X0 < X1 along it. Box: a support seen the same way.
    private sealed record Seg(double Y, double X0, double X1);

    private sealed record Box(double MinX, double MaxX, double MinY, double MaxY, double CenterX, Support Support);

    // Beams running along X (u, or v with the frame transposed).
    private static List<Beam> Along(List<Seg> segs, List<Box> boxes, bool alongU, Options o, List<Dangling> dangling)
    {
        segs.Sort((a, b) => a.Y.CompareTo(b.Y));
        var ys = segs.Select(s => s.Y).ToArray();
        IEnumerable<Seg> Band(double minY, double maxY)
        {
            var i = Array.BinarySearch(ys, minY);
            if (i < 0) i = ~i;
            while (i > 0 && ys[i - 1] >= minY) i--;
            for (; i < ys.Length && ys[i] <= maxY; i++) yield return segs[i];
        }

        var found = new Dictionary<(int, int, long), Beam>();
        foreach (var box in boxes.Where(b => b.Support.IsColumn))
        foreach (var dir in new[] { 1, -1 })
        {
            var face = dir > 0 ? box.MaxX : -box.MinX;
            var near = Band(box.MinY - o.MaxWidthMm / 2, box.MaxY + o.MaxWidthMm / 2)
                .Where(s => P1(s, dir) >= face - o.ProbeMm && P0(s, dir) <= face + o.ProbeMm)
                .ToList();
            var edges = Distinct(near.Select(s => s.Y), o.EdgeToleranceMm);
            Beam? best = null;
            Dangling? longest = null;
            foreach (var (y1, y2) in Pairs(edges, o))
            {
                var middle = (y1 + y2) / 2;
                if (middle < box.MinY - o.EdgeToleranceMm || middle > box.MaxY + o.EdgeToleranceMm) continue;
                var edge1 = Band(y1 - o.EdgeToleranceMm, y1 + o.EdgeToleranceMm).ToList();
                var edge2 = Band(y2 - o.EdgeToleranceMm, y2 + o.EdgeToleranceMm).ToList();
                var reach = Math.Min(Reach(edge1, face, dir, o), Reach(edge2, face, dir, o));
                var target = boxes
                    .Where(b => b != box && (dir > 0 ? b.MinX : -b.MaxX) >= face - o.EdgeToleranceMm
                        && middle >= b.MinY - o.EdgeToleranceMm && middle <= b.MaxY + o.EdgeToleranceMm)
                    .MinBy(b => dir > 0 ? b.MinX : -b.MaxX);
                var targetFace = target is null ? double.MaxValue : dir > 0 ? target.MinX : -target.MaxX;
                if (target is not null && reach >= targetFace - o.MaxGapMm && targetFace - face > 1)
                {
                    var coverage = (Covered(edge1, face, targetFace, dir) + Covered(edge2, face, targetFace, dir)) / (2 * (targetFace - face));
                    if (coverage < o.MinCoverage) continue;
                    var beam = new Beam(box.Support, target.Support, alongU, middle, box.CenterX, target.CenterX, y2 - y1,
                        Math.Min(1, coverage), null, false);
                    if (best is null || beam.Coverage > best.Coverage + 1e-9
                        || (Math.Abs(beam.Coverage - best.Coverage) <= 1e-9 && Math.Abs(middle - Mid(box)) < Math.Abs(best.Cross - Mid(box))))
                        best = beam;
                }
                else if (reach - face >= o.MinDanglingMm && (longest is null || reach - face > longest.LengthMm))
                    longest = new Dangling(box.Support, alongU, middle, Math.Min(reach, targetFace) - face);
            }
            if (best is not null)
            {
                var (a, b) = (boxes.IndexOf(box), boxes.FindIndex(x => x.Support == best.To));
                var key = (Math.Min(a, b), Math.Max(a, b), (long)Math.Round(best.Cross / 10));
                if (!found.TryGetValue(key, out var other) || best.Coverage > other.Coverage) found[key] = best;
            }
            else if (longest is not null)
                dangling.Add(longest);
        }
        return found.Values.ToList();
    }

    private static double Mid(Box box) => (box.MinY + box.MaxY) / 2;

    // A segment's ends along the walking direction: dir = -1 mirrors X so the walk always goes up.
    private static double P0(Seg s, int dir) => dir > 0 ? s.X0 : -s.X1;
    private static double P1(Seg s, int dir) => dir > 0 ? s.X1 : -s.X0;

    // How far an edge is drawn from the face: segments starting within probe of it, then gaps up to maxGapMm.
    private static double Reach(List<Seg> edge, double face, int dir, Options o)
    {
        var reach = face;
        var started = false;
        foreach (var s in edge.OrderBy(s => P0(s, dir)))
        {
            if (P1(s, dir) <= reach) continue;
            var limit = started ? reach + o.MaxGapMm : face + o.ProbeMm;
            if (P0(s, dir) > limit) break;
            reach = Math.Max(reach, P1(s, dir));
            started = true;
        }
        return reach;
    }

    // Length of [from, to] drawn by the edge's segments (overlaps counted once).
    private static double Covered(List<Seg> edge, double from, double to, int dir)
    {
        double covered = 0, end = from;
        foreach (var s in edge.OrderBy(s => P0(s, dir)))
        {
            var (a, b) = (Math.Max(P0(s, dir), end), Math.Min(P1(s, dir), to));
            if (b <= a) continue;
            covered += b - a;
            end = b;
        }
        return covered;
    }

    private static List<double> Distinct(IEnumerable<double> values, double tolerance)
    {
        var result = new List<double>();
        foreach (var value in values.OrderBy(v => v))
            if (result.Count == 0 || value - result[^1] > tolerance) result.Add(value);
        return result;
    }

    // Neighbouring edge positions that can be a beam; a line midway between two others is a centre line, so the
    // outer pair is the beam and its halves are not.
    private static List<(double, double)> Pairs(List<double> ys, Options o)
    {
        bool Width(double a, double b) => b - a >= o.MinWidthMm - o.EdgeToleranceMm && b - a <= o.MaxWidthMm + o.EdgeToleranceMm;
        var pairs = new List<(double, double)>();
        var halves = new HashSet<int>();
        for (var i = 0; i + 2 < ys.Count; i++)
            if (Width(ys[i], ys[i + 2]) && Math.Abs(ys[i + 1] - (ys[i] + ys[i + 2]) / 2) <= o.EdgeToleranceMm)
            {
                pairs.Add((ys[i], ys[i + 2]));
                halves.Add(i);
                halves.Add(i + 1);
            }
        for (var i = 0; i + 1 < ys.Count; i++)
            if (!halves.Contains(i) && Width(ys[i], ys[i + 1])) pairs.Add((ys[i], ys[i + 1]));
        return pairs;
    }

    // Both ends inside one support (outline, hatch or face of a column or wall), so not a beam edge.
    private static bool Inside(Line line, IReadOnlyList<Support> supports, double t) =>
        supports.Any(s => Contains(s, line.U0, line.V0, t) && Contains(s, line.U1, line.V1, t));

    private static bool Contains(Support s, double u, double v, double t) =>
        u >= s.MinU - t && u <= s.MaxU + t && v >= s.MinV - t && v <= s.MaxV + t;

    // Each mark goes to the nearest beam parallel to it (any beam when the text direction is unknown) within
    // tagDistanceMm across and beside its span; a beam keeps its nearest mark. Returns the marks no beam took.
    private static List<BeamTag> AssignTags(List<Beam> beams, IReadOnlyList<Text> texts, Options o)
    {
        var best = new Dictionary<int, (BeamTag Tag, double Distance)>();
        var unused = new List<BeamTag>();
        foreach (var text in texts)
        {
            if (BeamTags.Parse(text.Content) is not { } tag) continue;
            bool? textAlongU = text.AngleRad is { } angle
                ? Math.Abs(Math.Sin(angle)) < 0.17 ? true : Math.Abs(Math.Cos(angle)) < 0.17 ? false : null
                : null;
            var candidate = beams
                .Select((beam, index) =>
                {
                    var (cross, along) = beam.AlongU ? (text.V, text.U) : (text.U, text.V);
                    var (lo, hi) = (Math.Min(beam.Start, beam.End), Math.Max(beam.Start, beam.End));
                    var ok = (textAlongU is null || textAlongU == beam.AlongU)
                        && along >= lo - o.ProbeMm && along <= hi + o.ProbeMm
                        && Math.Abs(cross - beam.Cross) <= o.TagDistanceMm;
                    return (index, ok, distance: Math.Abs(cross - beam.Cross));
                })
                .Where(c => c.ok)
                .OrderBy(c => c.distance)
                .FirstOrDefault();
            if (!candidate.ok)
            {
                unused.Add(tag);
                continue;
            }
            if (best.TryGetValue(candidate.index, out var held))
            {
                if (candidate.distance >= held.Distance)
                {
                    unused.Add(tag);
                    continue;
                }
                unused.Add(held.Tag);
            }
            best[candidate.index] = (tag, candidate.distance);
        }
        foreach (var (index, entry) in best) beams[index] = beams[index] with { Tag = entry.Tag };
        return unused;
    }

    // A mark spreads along the line of beams it labels: to an unmarked beam in line (same direction and cross
    // position, same width within 10 mm) that shares a support, then on from there, nearest mark first.
    private static void Spread(List<Beam> beams, Options o)
    {
        var queue = new Queue<int>(Enumerable.Range(0, beams.Count).Where(i => beams[i].Tag is not null));
        while (queue.Count > 0)
        {
            var i = queue.Dequeue();
            var from = beams[i];
            for (var j = 0; j < beams.Count; j++)
            {
                var to = beams[j];
                if (to.Tag is not null || to.AlongU != from.AlongU) continue;
                if (Math.Abs(to.Cross - from.Cross) > o.EdgeToleranceMm * 2 || Math.Abs(to.WidthMm - from.WidthMm) > 10) continue;
                var shared = to.From == from.From || to.From == from.To || to.To == from.From || to.To == from.To;
                if (!shared) continue;
                beams[j] = to with { Tag = from.Tag, TagInherited = true };
                queue.Enqueue(j);
            }
        }
    }
}
