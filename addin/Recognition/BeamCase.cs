using System.Globalization;

namespace Bimtech.RevitMcp.Recognition;

// Beam recognition on plain data: turns model-coordinate lines, texts and supports (mm) into the grid frame
// BeamRecognizer works in, and back. Used by propose_beams (live model) and by Run (a saved CadCase), so a case
// replays exactly what Revit did and its approved beams become a regression test.
public static class BeamCase
{
    // The grid frame: u along one grid family at angle theta from X, v perpendicular; grids in mm in that frame.
    public sealed record Frame(double Cos, double Sin, IReadOnlyList<GridIntersections.Axis> AlongU, IReadOnlyList<GridIntersections.Axis> AlongV)
    {
        public static readonly Frame Plain = new(1, 0, [], []);

        public double U(double x, double y) => x * Cos + y * Sin;
        public double V(double x, double y) => -x * Sin + y * Cos;
        public (double X, double Y) Model(double u, double v) => (u * Cos - v * Sin, u * Sin + v * Cos);
        public double ThetaRad => Math.Atan2(Sin, Cos);
    }

    // A recognized beam with its axis back in model coordinates.
    public sealed record Found(BeamRecognizer.Beam Beam, double X0, double Y0, double X1, double Y1)
    {
        public string Span => $"{Beam.From.Label} → {Beam.To.Label}";
    }

    public sealed record Result(BeamRecognizer.Result Recognition, List<Found> Beams, int TruthCount, int Matched, List<string> Missing, List<string> Extra)
    {
        public bool IsPerfect => Missing.Count == 0 && Extra.Count == 0;
    }

    // Two beams are the same when both ends agree within this (either direction).
    public const double MatchToleranceMm = 100;

    // The frame of a set of grids: theta from all their directions (averaged on 4·angle, so 0° and 90° agree),
    // each grid sorted into the family it runs along. No grids: the model axes.
    public static Frame FrameOf(IReadOnlyList<TruthGrid> grids)
    {
        if (grids.Count == 0) return Frame.Plain;
        double c = 0, s = 0;
        foreach (var grid in grids)
        {
            var angle = Math.Atan2(grid.Y1 - grid.Y0, grid.X1 - grid.X0);
            c += Math.Cos(4 * angle);
            s += Math.Sin(4 * angle);
        }
        var theta = Math.Atan2(s, c) / 4;
        var frame = new Frame(Math.Cos(theta), Math.Sin(theta), [], []);
        var tolerance = Math.Sin(2 * Math.PI / 180);
        var alongU = new List<GridIntersections.Axis>();
        var alongV = new List<GridIntersections.Axis>();
        foreach (var grid in grids)
        {
            var (du, dv) = (frame.U(grid.X1, grid.Y1) - frame.U(grid.X0, grid.Y0), frame.V(grid.X1, grid.Y1) - frame.V(grid.X0, grid.Y0));
            var length = Math.Sqrt(du * du + dv * dv);
            if (length < 1) continue;
            if (Math.Abs(dv) / length <= tolerance)
                alongU.Add(new(grid.Name, frame.V(grid.X0, grid.Y0), frame.U(grid.X0, grid.Y0), frame.U(grid.X1, grid.Y1)));
            else if (Math.Abs(du) / length <= tolerance)
                alongV.Add(new(grid.Name, frame.U(grid.X0, grid.Y0), frame.V(grid.X0, grid.Y0), frame.V(grid.X1, grid.Y1)));
        }
        return frame with { AlongU = alongU, AlongV = alongV };
    }

    // Supports in the frame, labelled "A-1" at a grid crossing, else by mark or id. A beam runs along its longer side.
    public static List<BeamRecognizer.Support> Supports(IEnumerable<CaseSupport> supports, Frame frame) =>
        supports.Where(s => s.Points.Count > 0).Select(s =>
        {
            var us = s.Points.Select(p => frame.U(p[0], p[1])).ToList();
            var vs = s.Points.Select(p => frame.V(p[0], p[1])).ToList();
            var (minU, maxU, minV, maxV) = (us.Min(), us.Max(), vs.Min(), vs.Max());
            var (cu, cv) = (frame.U(s.Cx, s.Cy), frame.V(s.Cx, s.Cy));
            var isColumn = s.Kind is not ("wall" or "beam");
            bool? beamAlongU = s.Kind == "beam" ? maxU - minU >= maxV - minV : null;
            return new BeamRecognizer.Support(Label(s, frame, cu, cv, maxU - minU, maxV - minV, isColumn),
                minU, maxU, minV, maxV, cu, cv, isColumn, s.Kind, beamAlongU, s.HeightMm);
        }).ToList();

    // A beam of the model as a support for secondary beams: the rectangle of its axis and width b.
    public static CaseSupport BeamSupport(TruthBeam beam, string? id = null)
    {
        var (dx, dy) = (beam.X1 - beam.X0, beam.Y1 - beam.Y0);
        var length = Math.Max(Math.Sqrt(dx * dx + dy * dy), 1e-9);
        var half = (beam.BMm ?? 200) / 2;
        var (nx, ny) = (-dy / length * half, dx / length * half);
        return new CaseSupport
        {
            Kind = "beam", Id = id, Mark = beam.Mark, Cx = (beam.X0 + beam.X1) / 2, Cy = (beam.Y0 + beam.Y1) / 2, HeightMm = beam.HMm,
            Points = [[beam.X0 + nx, beam.Y0 + ny], [beam.X1 + nx, beam.Y1 + ny], [beam.X1 - nx, beam.Y1 - ny], [beam.X0 - nx, beam.Y0 - ny]],
        };
    }

    public static Result Run(CadCase data, BeamRecognizer.Options? options = null)
    {
        var frame = FrameOf(data.Truth?.Grids ?? []);
        var lines = new List<BeamRecognizer.Line>();
        void Add(double x0, double y0, double x1, double y1) =>
            lines.Add(new(frame.U(x0, y0), frame.V(x0, y0), frame.U(x1, y1), frame.V(x1, y1)));
        foreach (var line in data.Lines) Add(line.X0, line.Y0, line.X1, line.Y1);
        foreach (var polyline in data.Polylines)
        {
            var points = polyline.Points;
            for (var i = 0; i + 1 < points.Count; i++) Add(points[i][0], points[i][1], points[i + 1][0], points[i + 1][1]);
            if (polyline.Closed && points.Count > 2) Add(points[^1][0], points[^1][1], points[0][0], points[0][1]);
        }
        var texts = data.Texts.Select(t => new BeamRecognizer.Text(t.Text, frame.U(t.X, t.Y), frame.V(t.X, t.Y),
            t.RotationDeg * Math.PI / 180 - frame.ThetaRad)).ToList();

        var recognition = BeamRecognizer.Recognize(lines, Supports(data.Model?.Supports ?? [], frame), texts, options);
        var found = recognition.Beams.Select(beam => ToModel(beam, frame)).ToList();

        var truth = data.Truth?.Beams ?? [];
        var unmatched = found.ToList();
        var missing = new List<string>();
        foreach (var beam in truth)
        {
            var match = unmatched.FirstOrDefault(f => Same(f, beam));
            if (match is null) missing.Add($"{beam.Mark ?? "?"} ({Mm(beam.X0)}, {Mm(beam.Y0)}) → ({Mm(beam.X1)}, {Mm(beam.Y1)})");
            else unmatched.Remove(match);
        }
        return new Result(recognition, found, truth.Count, truth.Count - missing.Count, missing,
            unmatched.Select(f => $"{f.Span} ({Mm(f.X0)}, {Mm(f.Y0)}) → ({Mm(f.X1)}, {Mm(f.Y1)})").ToList());
    }

    public static Found ToModel(BeamRecognizer.Beam beam, Frame frame)
    {
        var (a, b) = beam.AlongU
            ? (frame.Model(beam.Start, beam.Cross), frame.Model(beam.End, beam.Cross))
            : (frame.Model(beam.Cross, beam.Start), frame.Model(beam.Cross, beam.End));
        return new Found(beam, a.X, a.Y, b.X, b.Y);
    }

    private static bool Same(Found found, TruthBeam truth)
    {
        bool Near(double x0, double y0, double x1, double y1) => Math.Sqrt((x0 - x1) * (x0 - x1) + (y0 - y1) * (y0 - y1)) <= MatchToleranceMm;
        return (Near(found.X0, found.Y0, truth.X0, truth.Y0) && Near(found.X1, found.Y1, truth.X1, truth.Y1))
            || (Near(found.X0, found.Y0, truth.X1, truth.Y1) && Near(found.X1, found.Y1, truth.X0, truth.Y0));
    }

    private static string Label(CaseSupport support, Frame frame, double u, double v, double sizeU, double sizeV, bool isColumn)
    {
        if (isColumn && frame.AlongU.Count > 0 && frame.AlongV.Count > 0)
        {
            var fitU = GridIntersections.Fit(u, sizeU / 2, frame.AlongV, 0);
            var fitV = GridIntersections.Fit(v, sizeV / 2, frame.AlongU, 0);
            if (fitU.OnGrid && fitV.OnGrid) return GridIntersections.Label(fitU.GridName!, fitV.GridName!);
        }
        var kind = isColumn ? "cột" : support.Kind == "beam" ? "dầm" : "vách";
        return support.Mark is not null ? $"{kind} {support.Mark}" : $"{kind} #{support.Id ?? "?"}";
    }

    private static string Mm(double value) => value.ToString("0", CultureInfo.InvariantCulture);
}
