namespace Bimtech.RevitMcp.Recognition;

// Where a DWG read from disk sits in the Revit model: model = rotate(Scale · drawing, RotationDeg) + (Dx, Dy), mm.
public sealed record DwgPlacement(double Scale, double RotationDeg, double Dx, double Dy)
{
    public (double X, double Y) Map(double x, double y)
    {
        var radians = RotationDeg * Math.PI / 180;
        var (cos, sin) = (Math.Cos(radians), Math.Sin(radians));
        return (Math.Round(Scale * (x * cos - y * sin) + Dx, 2), Math.Round(Scale * (x * sin + y * cos) + Dy, 2));
    }
}

// Finds the placement by matching the lines Revit reports for the CAD (model coordinates) with the lines of the
// file, instead of trusting how Revit placed or scaled the import: long lines of the model vote for the shift that
// lays an equally long, parallel line of the file onto them, then a sample of all model lines checks the winner.
// Rotation comes from the import's transform (tried first) with the quarter turns as fallbacks; the scale from a
// short list of unit changes, since the file is already in mm by its own units.
public sealed record DwgAlignment(DwgPlacement? Placement, int Checked, int Matched, string? Problem)
{
    // Below this share of model lines found in the placed drawing, the placement is not trusted.
    public const double MinMatchRate = 0.7;
    public const double ToleranceMm = 1.0;

    public static readonly double[] Scales = [1, 10, 0.1, 1000, 0.001, 25.4, 1 / 25.4, 304.8, 1 / 304.8];

    private const int MaxVoters = 400;
    private const int MaxChecked = 3000;
    private const double MinVoterLengthMm = 200;
    // A length shared by more lines of the file than this (hatching, repeated details) says nothing about the shift.
    private const int MaxCandidatesPerVoter = 64;

    public double MatchRate => Checked == 0 ? 0 : (double)Matched / Checked;

    public static DwgAlignment Find(IReadOnlyList<Shapes.Segment> model, IReadOnlyList<Shapes.Segment> drawing, double rotationDeg)
    {
        if (model.Count == 0) return new DwgAlignment(null, 0, 0, "Revit reports no lines for this CAD to compare with.");
        if (drawing.Count == 0) return new DwgAlignment(null, 0, 0, "The file has no lines in model space.");

        var rotations = new[] { rotationDeg, rotationDeg + 90, rotationDeg + 180, rotationDeg + 270 }
            .Select(r => Math.Round(((r % 360) + 360) % 360, 6)).Distinct().ToList();
        DwgAlignment? best = null;
        foreach (var scale in Scales)
            foreach (var rotation in rotations)
            {
                var candidate = Try(model, drawing, scale, rotation);
                if (candidate.Placement is null) continue;
                if (best is null || candidate.Matched > best.Matched) best = candidate;
                if (best.MatchRate >= MinMatchRate) return best;
            }
        if (best is null)
            return new DwgAlignment(null, 0, 0,
                "No line of the file matches a long line of the CAD in Revit at any tried scale and rotation: is this the right file?");
        return best with
        {
            Problem = $"Only {best.MatchRate:P0} of the CAD's lines in Revit are found in the placed file (need {MinMatchRate:P0}): "
                + "the file may differ from the one imported (edited since, other xrefs) or Revit placed it unusually.",
        };
    }

    private static DwgAlignment Try(IReadOnlyList<Shapes.Segment> model, IReadOnlyList<Shapes.Segment> drawing, double scale, double rotation)
    {
        var turn = new DwgPlacement(scale, rotation, 0, 0);
        var placed = drawing.Select(s =>
        {
            var (x0, y0) = Turn(turn, s.X0, s.Y0);
            var (x1, y1) = Turn(turn, s.X1, s.Y1);
            return new Shapes.Segment(x0, y0, x1, y1);
        }).ToList();

        var byLength = new Dictionary<long, List<int>>();
        for (var i = 0; i < placed.Count; i++)
        {
            var key = (long)Math.Round(placed[i].Length);
            if (!byLength.TryGetValue(key, out var list)) byLength[key] = list = [];
            list.Add(i);
        }

        // Votes for the shift, in whole mm.
        var votes = new Dictionary<(long, long), List<(double Dx, double Dy)>>();
        foreach (var line in model.Where(s => s.Length >= MinVoterLengthMm).OrderByDescending(s => s.Length).Take(MaxVoters))
        {
            var key = (long)Math.Round(line.Length);
            var candidates = Enumerable.Range(-1, 3)
                .SelectMany(offset => byLength.TryGetValue(key + offset, out var list) ? list : [])
                .ToList();
            if (candidates.Count is 0 or > MaxCandidatesPerVoter) continue;
            foreach (var index in candidates)
            {
                var other = placed[index];
                if (Math.Abs(other.Length - line.Length) > ToleranceMm) continue;
                foreach (var (ax, ay, bx, by) in new[] { (other.X0, other.Y0, other.X1, other.Y1), (other.X1, other.Y1, other.X0, other.Y0) })
                {
                    var (dx, dy) = (line.X0 - ax, line.Y0 - ay);
                    if (Math.Abs(line.X1 - bx - dx) > ToleranceMm || Math.Abs(line.Y1 - by - dy) > ToleranceMm) continue;
                    var bin = ((long)Math.Round(dx), (long)Math.Round(dy));
                    if (!votes.TryGetValue(bin, out var list)) votes[bin] = list = [];
                    list.Add((dx, dy));
                }
            }
        }
        if (votes.Count == 0) return new DwgAlignment(null, 0, 0, null);

        // The bin with the most votes, counting its neighbours (a shift near a half mm splits between two bins).
        int Around((long X, long Y) bin) =>
            Enumerable.Range(-1, 3).Sum(i => Enumerable.Range(-1, 3).Sum(j => votes.TryGetValue((bin.X + i, bin.Y + j), out var l) ? l.Count : 0));
        var winner = votes.Keys.MaxBy(Around);
        var near = Enumerable.Range(-1, 3).SelectMany(i => Enumerable.Range(-1, 3)
                .SelectMany(j => votes.TryGetValue((winner.Item1 + i, winner.Item2 + j), out var l) ? l : []))
            .ToList();
        var placement = turn with { Dx = Math.Round(near.Average(v => v.Dx), 2), Dy = Math.Round(near.Average(v => v.Dy), 2) };

        // Check: model lines (spread over the whole drawing) whose two ends match a placed line of the file.
        var cell = 10.0;
        var starts = new Dictionary<(long, long), List<int>>();
        for (var i = 0; i < placed.Count; i++)
            foreach (var (x, y) in new[] { (placed[i].X0, placed[i].Y0), (placed[i].X1, placed[i].Y1) })
            {
                var key = ((long)Math.Floor((x + placement.Dx) / cell), (long)Math.Floor((y + placement.Dy) / cell));
                if (!starts.TryGetValue(key, out var list)) starts[key] = list = [];
                list.Add(i);
            }
        bool Close(double x0, double y0, double x1, double y1) =>
            Math.Abs(x0 - x1) <= ToleranceMm && Math.Abs(y0 - y1) <= ToleranceMm;
        bool Found(Shapes.Segment line)
        {
            var (cx, cy) = ((long)Math.Floor(line.X0 / cell), (long)Math.Floor(line.Y0 / cell));
            for (var i = -1; i <= 1; i++)
                for (var j = -1; j <= 1; j++)
                {
                    if (!starts.TryGetValue((cx + i, cy + j), out var list)) continue;
                    foreach (var index in list)
                    {
                        var s = placed[index];
                        var (ax, ay, bx, by) = (s.X0 + placement.Dx, s.Y0 + placement.Dy, s.X1 + placement.Dx, s.Y1 + placement.Dy);
                        if ((Close(line.X0, line.Y0, ax, ay) && Close(line.X1, line.Y1, bx, by))
                            || (Close(line.X0, line.Y0, bx, by) && Close(line.X1, line.Y1, ax, ay)))
                            return true;
                    }
                }
            return false;
        }
        var checkable = model.Where(s => s.Length >= 10).ToList();
        var step = Math.Max(1, checkable.Count / MaxChecked);
        var sample = checkable.Where((_, i) => i % step == 0).ToList();
        return new DwgAlignment(placement, sample.Count, sample.Count(Found), null);
    }

    private static (double X, double Y) Turn(DwgPlacement turn, double x, double y)
    {
        var radians = turn.RotationDeg * Math.PI / 180;
        var (cos, sin) = (Math.Cos(radians), Math.Sin(radians));
        return (turn.Scale * (x * cos - y * sin), turn.Scale * (x * sin + y * cos));
    }

    // The straight pieces of a case: its lines and the edges of its polylines (Revit reports a DWG polyline as one).
    public static List<Shapes.Segment> Segments(CadCase data)
    {
        var segments = data.Lines.Select(l => new Shapes.Segment(l.X0, l.Y0, l.X1, l.Y1)).ToList();
        foreach (var polyline in data.Polylines)
        {
            var points = polyline.Points;
            var count = polyline.Closed ? points.Count : points.Count - 1;
            for (var i = 0; i < count; i++)
            {
                var (a, b) = (points[i], points[(i + 1) % points.Count]);
                segments.Add(new Shapes.Segment(a[0], a[1], b[0], b[1]));
            }
        }
        return segments;
    }

    // Copies the file's hatches and texts, placed on the model, into a case read from Revit; their layers are
    // added to its styles.
    public static void AddTo(CadCase target, CadCase drawing, DwgPlacement placement)
    {
        var styles = new Dictionary<int, int>();
        int StyleOf(int index)
        {
            if (styles.TryGetValue(index, out var mapped)) return mapped;
            var style = drawing.Styles[index];
            mapped = target.Styles.FindIndex(s => s.Layer == style.Layer && s.LinePattern == style.LinePattern && s.Category == style.Category);
            if (mapped < 0)
            {
                target.Styles.Add(style);
                mapped = target.Styles.Count - 1;
            }
            return styles[index] = mapped;
        }
        double[] Point(double[] p)
        {
            var (x, y) = placement.Map(p[0], p[1]);
            return [x, y];
        }

        foreach (var hatch in drawing.Hatches)
            target.Hatches.Add(new CadHatch
            {
                Style = StyleOf(hatch.Style), Pattern = hatch.Pattern, IsSolid = hatch.IsSolid,
                Loops = hatch.Loops.Select(loop => loop.Select(Point).ToList()).ToList(),
            });
        foreach (var text in drawing.Texts)
        {
            var (x, y) = placement.Map(text.X, text.Y);
            target.Texts.Add(new CadText
            {
                Style = text.Style is { } style ? StyleOf(style) : null, Text = text.Text, X = x, Y = y,
                HeightMm = Math.Round(text.HeightMm * placement.Scale, 1),
                RotationDeg = Math.Round(((text.RotationDeg + placement.RotationDeg) % 360 + 360) % 360, 4),
            });
        }
    }
}
