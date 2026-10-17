namespace Bimtech.RevitMcp.Recognition;

// How sure a secondary beam is: a beam resting on at least one beam (BeamRecognizer with Options.Secondary), weighed on
// evidence the way an engineer checks it, each with its reason for the review window:
//   + both ends on supports (always: only support-to-support spans are found), a mark, at an even division of the
//     supporting beam's span (half, thirds, quarters) or on a grid, drawn dashed (under the slab), continued in line
//     by another secondary beam;
//   − wider or deeper than a beam it rests on (a secondary beam is never bigger than its support), lying on a wall or
//     masonry hatch (a 220 wall drawn as two lines looks like a 220 beam), a span under 1 m, edges drawn broken.
// Score ≥ SureScore: sure; below: to review. All lengths mm, in the grid frame.
public static class SecondaryBeams
{
    public const int SureScore = 3;
    // Edges drawing less than this of the span are "broken".
    public const double WellDrawn = 0.9;
    public const double MinSpanMm = 1000;
    // An even division of the support span within this share of it.
    private const double DivisionTolerance = 0.03;
    private const double SizeToleranceMm = 10;
    private static readonly double[] Divisions = [1.0 / 2, 1.0 / 3, 2.0 / 3, 1.0 / 4, 3.0 / 4];

    public const string SupportsReason = "hai đầu tựa lên gối (dầm, cột, vách)";
    public const string MarkReason = "có ký hiệu dầm";
    public const string DivisionReason = "chia đều nhịp dầm gối";
    public const string GridReason = "nằm trên trục";
    public const string DashedReason = "vẽ nét khuất";
    public const string InLineReason = "nối tiếp thành dải với dầm phụ khác";
    public const string BiggerReason = "to hơn dầm gối của nó";
    public const string WallReason = "trùng hatch tường hay vách";
    public const string ShortReason = "nhịp ngắn dưới 1 m";
    public const string BrokenReason = "nét vẽ đứt nhiều";

    public sealed record Weight(int Score, bool Sure, List<string> Reasons);

    // beams: every beam of the round (to find the ones continuing this one). frame: the grids. wallLoops: outer loops of
    // wall-like hatches (thin strips) in the frame's (u, v), empty when the DWG was not read.
    public static Weight Weigh(BeamRecognizer.Beam beam, IReadOnlyList<BeamRecognizer.Beam> beams, BeamCase.Frame frame,
        IReadOnlyList<List<(double U, double V)>> wallLoops)
    {
        var reasons = new List<string>();
        var score = 0;
        void Add(int points, string reason)
        {
            score += points;
            reasons.Add($"{(points > 0 ? "+" : "")}{points} {reason}");
        }

        Add(2, SupportsReason);
        if (beam.Tag?.Name is not null) Add(1, MarkReason);
        if (EvenDivision(beam)) Add(1, DivisionReason);
        else if ((beam.AlongU ? frame.AlongU : frame.AlongV).Any(grid => Math.Abs(grid.PositionMm - beam.Cross) <= GridIntersections.DefaultToleranceMm))
            Add(1, GridReason);
        if (beam.Dashed) Add(1, DashedReason);
        if (beams.Any(other => !ReferenceEquals(other, beam) && other.IsSecondary && other.AlongU == beam.AlongU
                && Math.Abs(other.Cross - beam.Cross) <= SizeToleranceMm
                && (other.From == beam.From || other.From == beam.To || other.To == beam.From || other.To == beam.To)))
            Add(1, InLineReason);

        var depth = beam.Tag?.HMm;
        var width = beam.Tag?.BMm ?? beam.WidthMm;
        if (new[] { beam.From, beam.To }.Any(s => s.IsBeam
                && (width > s.WidthMm + SizeToleranceMm || (depth is { } h && s.HeightMm is { } sh && h > sh + SizeToleranceMm))))
            Add(-3, BiggerReason);
        var (mu, mv) = beam.AlongU ? ((beam.Start + beam.End) / 2, beam.Cross) : (beam.Cross, (beam.Start + beam.End) / 2);
        if (wallLoops.Any(loop => Inside(loop, mu, mv))) Add(-2, WallReason);
        if (beam.SpanMm < MinSpanMm) Add(-1, ShortReason);
        if (beam.Coverage < WellDrawn) Add(-1, BrokenReason);
        return new Weight(score, score >= SureScore, reasons);
    }

    // The beam crosses a beam it rests on at half, a third or a quarter of that beam's span.
    private static bool EvenDivision(BeamRecognizer.Beam beam) =>
        new[] { beam.From, beam.To }.Where(s => s.IsBeam && s.BeamAlongU != beam.AlongU).Any(s =>
        {
            var (start, end) = s.BeamAlongU == true ? (s.MinU, s.MaxU) : (s.MinV, s.MaxV);
            var length = end - start;
            if (length < 1) return false;
            var at = (beam.Cross - start) / length;
            return Divisions.Any(d => Math.Abs(at - d) <= DivisionTolerance);
        });

    // Thin hatches (mean thickness 2·area/perimeter up to maxThicknessMm, longer than minLengthMm): walls and masonry.
    public static List<List<(double U, double V)>> WallLoops(IEnumerable<List<double[]>> loops, BeamCase.Frame frame,
        double maxThicknessMm = 400, double minLengthMm = 1000)
    {
        var walls = new List<List<(double U, double V)>>();
        foreach (var loop in loops.Where(loop => loop.Count >= 3))
        {
            double area = 0, perimeter = 0;
            for (var i = 0; i < loop.Count; i++)
            {
                var (a, b) = (loop[i], loop[(i + 1) % loop.Count]);
                area += a[0] * b[1] - b[0] * a[1];
                perimeter += Math.Sqrt(Math.Pow(b[0] - a[0], 2) + Math.Pow(b[1] - a[1], 2));
            }
            if (perimeter < 1e-9 || 2 * Math.Abs(area / 2) / perimeter > maxThicknessMm) continue;
            var points = loop.Select(p => (frame.U(p[0], p[1]), frame.V(p[0], p[1]))).ToList();
            var longest = Math.Max(points.Max(p => p.Item1) - points.Min(p => p.Item1), points.Max(p => p.Item2) - points.Min(p => p.Item2));
            if (longest >= minLengthMm) walls.Add(points);
        }
        return walls;
    }

    private static bool Inside(IReadOnlyList<(double U, double V)> polygon, double u, double v)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            if ((polygon[i].V > v) != (polygon[j].V > v)
                && u < (polygon[j].U - polygon[i].U) * (v - polygon[i].V) / (polygon[j].V - polygon[i].V) + polygon[i].U)
                inside = !inside;
        return inside;
    }
}
