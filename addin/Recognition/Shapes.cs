namespace Bimtech.RevitMcp.Recognition;

// Plain-geometry checks for CAD outlines, in mm, with no Revit types so they can be tested.
public static class Shapes
{
    public sealed record Segment(double X0, double Y0, double X1, double Y1)
    {
        public double Length => Math.Sqrt((X1 - X0) * (X1 - X0) + (Y1 - Y0) * (Y1 - Y0));
    }

    // A rotated rectangle: centre, side lengths along its own axes, and the angle of the Length axis from X.
    public sealed record Rectangle(double Cx, double Cy, double Length, double Width, double AngleRadians);

    // True when the segments really draw this rectangle: every end point lies on its outline (within toleranceMm), and
    // each of the four sides is covered by segments running along it for at least `coverage` of its length. Lines
    // inside (hatching) are allowed since they end on the outline. An arrow, an L-shaped column, or a column merged
    // with a touching wall does not cover the sides of its bounding rectangle, so it is not a rectangle.
    public static bool IsRectangle(IReadOnlyList<Segment> segments, Rectangle rectangle, double toleranceMm = 5, double coverage = 0.9)
    {
        if (segments.Count == 0) return false;
        var (cos, sin) = (Math.Cos(rectangle.AngleRadians), Math.Sin(rectangle.AngleRadians));
        // Local coordinates: u along Length, v along Width, origin at the centre.
        (double U, double V) Local(double x, double y) =>
            ((x - rectangle.Cx) * cos + (y - rectangle.Cy) * sin, -(x - rectangle.Cx) * sin + (y - rectangle.Cy) * cos);
        var (halfU, halfV) = (rectangle.Length / 2, rectangle.Width / 2);

        bool OnOutline((double U, double V) p) =>
            (Math.Abs(Math.Abs(p.U) - halfU) <= toleranceMm && Math.Abs(p.V) <= halfV + toleranceMm)
            || (Math.Abs(Math.Abs(p.V) - halfV) <= toleranceMm && Math.Abs(p.U) <= halfU + toleranceMm);

        var covered = new double[4]; // +U side, -U side, +V side, -V side
        foreach (var segment in segments)
        {
            var (a, b) = (Local(segment.X0, segment.Y0), Local(segment.X1, segment.Y1));
            if (!OnOutline(a) || !OnOutline(b)) return false;
            for (var side = 0; side < 4; side++)
            {
                var alongU = side < 2; // sides ±U run along V, sides ±V run along U
                var offset = side % 2 == 0 ? (alongU ? halfU : halfV) : -(alongU ? halfU : halfV);
                var (ca, cb) = alongU ? (a.U, b.U) : (a.V, b.V);
                if (Math.Abs(ca - offset) <= toleranceMm && Math.Abs(cb - offset) <= toleranceMm)
                    covered[side] += alongU ? Math.Abs(b.V - a.V) : Math.Abs(b.U - a.U);
            }
        }
        return covered[0] >= coverage * rectangle.Width && covered[1] >= coverage * rectangle.Width
            && covered[2] >= coverage * rectangle.Length && covered[3] >= coverage * rectangle.Length;
    }

    // Groups segments whose end points touch (within toleranceMm) into connected clusters, as lists of indices.
    // End points are bucketed on a grid of the tolerance, so only nearby points are compared: linear in practice,
    // where comparing every pair would take minutes on a layer of tens of thousands of hatch lines.
    public static List<List<int>> Clusters(IReadOnlyList<Segment> segments, double toleranceMm)
    {
        var parent = Enumerable.Range(0, segments.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        void Union(int a, int b) => parent[Find(a)] = Find(b);

        var cell = Math.Max(toleranceMm, 1e-6);
        var buckets = new Dictionary<(long, long), List<(int Index, double X, double Y)>>();
        for (var i = 0; i < segments.Count; i++)
            foreach (var (x, y) in new[] { (segments[i].X0, segments[i].Y0), (segments[i].X1, segments[i].Y1) })
            {
                var (cx, cy) = ((long)Math.Floor(x / cell), (long)Math.Floor(y / cell));
                for (var dx = -1L; dx <= 1; dx++)
                    for (var dy = -1L; dy <= 1; dy++)
                        if (buckets.TryGetValue((cx + dx, cy + dy), out var near))
                            foreach (var other in near)
                                if (other.Index != i && Math.Abs(other.X - x) <= toleranceMm && Math.Abs(other.Y - y) <= toleranceMm
                                    && Math.Sqrt(Math.Pow(other.X - x, 2) + Math.Pow(other.Y - y, 2)) <= toleranceMm)
                                    Union(i, other.Index);
                if (!buckets.TryGetValue((cx, cy), out var list)) buckets[(cx, cy)] = list = [];
                list.Add((i, x, y));
            }

        return Enumerable.Range(0, segments.Count).GroupBy(Find).Select(group => group.ToList()).ToList();
    }
}
