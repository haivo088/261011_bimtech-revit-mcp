using System.IO;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;

namespace Bimtech.RevitMcp.Recognition;

// Reads a DWG or DXF file from disk into a CadCase in the drawing's own coordinates, in mm by the file's units:
// lines, arcs, circles, polylines, hatches with their boundary loops, and texts (block attributes included), with
// blocks expanded. The Revit API gives neither the hatches nor the texts of an imported DWG, and exploding the CAD
// in Revit sometimes loses the hatches. DwgAlignment then finds where the drawing sits in the model.
public static class DwgImport
{
    public const int MaxBlockDepth = 8;
    // Points per arc (or polyline bulge) in a hatch boundary or a polyline.
    private const int ArcPoints = 16;

    public static CadDocument Open(string path)
    {
        if (Path.GetExtension(path).Equals(".dxf", StringComparison.OrdinalIgnoreCase))
        {
            using var dxf = new DxfReader(path);
            return dxf.Read();
        }
        using var dwg = new DwgReader(path);
        return dwg.Read();
    }

    // Model space only (layouts hold title blocks and viewports, not the plan). Category names the file, like
    // the category Revit gives an import's layers.
    public static CadCase ToCase(CadDocument document, string category, int maxEntities = 2_000_000)
    {
        var result = new CadCase { CreatedAt = DateTimeOffset.Now };
        var builder = new Builder(result, UnitsToMm(document.Header.InsUnits.ToString()), category, maxEntities);
        foreach (var entity in document.ModelSpace.Entities) builder.Add(entity, 0, Affine.Identity);
        return result;
    }

    // Millimeters per drawing unit. A unitless drawing is taken as mm, the usual unit of Vietnamese plans.
    public static double UnitsToMm(string units) => units switch
    {
        "Inches" => 25.4,
        "Feet" => 304.8,
        "Centimeters" => 10,
        "Decimeters" => 100,
        "Meters" => 1000,
        _ => 1,
    };

    // Blocks are expanded here rather than with ACadSharp's Insert.Explode: Explode (3.8) gives a nested insert
    // that is mirrored and rotated a different placement (R(-90)·S(-1,1) comes back as R(-90)·S(1,-1), half a turn
    // off) and flips a mirrored hatch's normal without moving its points. Each entity is read in its own coordinates
    // and placed with toWorld, composed from the raw values of the inserts above it.
    private sealed class Builder(CadCase result, double scale, string category, int maxEntities)
    {
        private readonly Dictionary<(string Layer, string? Pattern), int> _styles = [];
        private int _entities;

        // toWorld: the entity's block coordinates to the drawing's world coordinates.
        public void Add(Entity entity, int depth, Affine toWorld)
        {
            if (entity.IsInvisible) return;
            if (_entities >= maxEntities)
            {
                Skip("over maxEntities");
                return;
            }
            switch (entity)
            {
                case Insert insert:
                    // Attribute values are real texts (grid names in bubbles, marks), stored where the insert sits;
                    // the block's attribute definitions are only their templates.
                    foreach (var attribute in insert.Attributes) AddText(attribute, toWorld);
                    if (depth >= MaxBlockDepth)
                    {
                        Skip("block nested too deep");
                        return;
                    }
                    if (insert.Block is null) return;
                    foreach (var placement in Placements(insert))
                        foreach (var part in insert.Block.Entities) Add(part, depth + 1, toWorld * placement);
                    return;
                case AttributeDefinition:
                    return;
                case Line line:
                    Count();
                    var (x0, y0) = Point(toWorld, line.StartPoint.X, line.StartPoint.Y);
                    var (x1, y1) = Point(toWorld, line.EndPoint.X, line.EndPoint.Y);
                    result.Lines.Add(new CadLine { Style = Style(entity), X0 = x0, Y0 = y0, X1 = x1, Y1 = y1 });
                    return;
                case Arc arc:
                    Count();
                    AddArc(entity, toWorld * Ocs(arc.Normal.Z), arc.Center.X, arc.Center.Y, arc.Radius, arc.StartAngle, arc.EndAngle);
                    return;
                case Circle circle:
                    Count();
                    AddArc(entity, toWorld * Ocs(circle.Normal.Z), circle.Center.X, circle.Center.Y, circle.Radius, null, null);
                    return;
                case Ellipse ellipse:
                    Count();
                    AddEllipse(ellipse, toWorld);
                    return;
                case LwPolyline polyline:
                    Count();
                    var ocs = toWorld * Ocs(polyline.Normal.Z);
                    result.Polylines.Add(new CadPolyline
                    {
                        Style = Style(entity),
                        Closed = polyline.IsClosed,
                        Points = Bulged(polyline.Vertices.Select(v => (v.Location.X, v.Location.Y, v.Bulge)).ToList(), polyline.IsClosed)
                            .Select(p => Point(ocs, p.X, p.Y)).Select(p => new[] { p.X, p.Y }).ToList(),
                    });
                    return;
                case Hatch hatch:
                    Count();
                    var plane = toWorld * Ocs(hatch.Normal.Z);
                    var loops = hatch.Paths
                        .Select(path => Loop(path.GetPoints(ArcPoints).Select(p => Point(plane, p.X, p.Y))))
                        .Where(loop => loop.Count >= 3)
                        .ToList();
                    if (loops.Count == 0)
                    {
                        Skip("Hatch without boundary");
                        return;
                    }
                    result.Hatches.Add(new CadHatch { Style = Style(entity, withPattern: false), Pattern = hatch.Pattern?.Name, IsSolid = hatch.IsSolid, Loops = loops });
                    return;
                case TextEntity text:
                    AddText(text, toWorld);
                    return;
                case MText text:
                    Count();
                    var value = string.Join(" ", text.GetPlainTextLines().Select(line => line.Trim()).Where(line => line.Length > 0));
                    if (value.Length == 0) return;
                    // Unlike a text, an MText's insert point and direction are in block coordinates, not in its plane's.
                    var (tx, ty) = Point(toWorld, text.InsertPoint.X, text.InsertPoint.Y);
                    var direction = text.AlignmentPoint;
                    var (dx, dy) = Math.Abs(direction.X) + Math.Abs(direction.Y) > 1e-9
                        ? toWorld.Turn(direction.X, direction.Y)
                        : (toWorld * Ocs(text.Normal.Z)).Turn(Math.Cos(text.Rotation), Math.Sin(text.Rotation));
                    result.Texts.Add(new CadText
                    {
                        Style = Style(entity, withPattern: false), Text = value, X = tx, Y = ty,
                        HeightMm = Math.Round(text.Height * scale * Length(toWorld.Turn(0, 1)), 1),
                        RotationDeg = Degrees(Math.Atan2(dy, dx)),
                    });
                    return;
                default:
                    Skip(entity.GetType().Name);
                    return;
            }
        }

        // Where an insert puts its block's coordinates, in the coordinates the insert sits in: the block's base point
        // to the insert point, scaled then rotated, in the insert's plane (x mirrored when its normal points down). A
        // multiple insert (MINSERT) repeats that along its rotated rows and columns.
        private static IEnumerable<Affine> Placements(Insert insert)
        {
            var basePoint = insert.Block.BlockEntity?.BasePoint ?? CSMath.XYZ.Zero;
            var turn = Affine.Rotate(insert.Rotation);
            var shape = turn * Affine.Scale(insert.XScale, insert.YScale) * Affine.Move(-basePoint.X, -basePoint.Y);
            var plane = Ocs(insert.Normal.Z);
            for (var row = 0; row < Math.Max(1, (int)insert.RowCount); row++)
                for (var column = 0; column < Math.Max(1, (int)insert.ColumnCount); column++)
                {
                    var (ox, oy) = turn.Turn(column * insert.ColumnSpacing, row * insert.RowSpacing);
                    yield return plane * Affine.Move(insert.InsertPoint.X + ox, insert.InsertPoint.Y + oy) * shape;
                }
        }

        // A 2D entity's own plane in its block's coordinates: drawn with the normal pointing down (mirrored in
        // AutoCAD), its x runs the other way.
        private static Affine Ocs(double normalZ) => normalZ < 0 ? Affine.Scale(-1, 1) : Affine.Identity;

        // An arc (or a circle, without angles) in its own plane, angles counterclockwise there. Placed by a mirror its
        // sense turns, so its ends swap; placed by an unequal scale it is an ellipse, kept as points.
        private void AddArc(Entity entity, Affine toWorld, double cx, double cy, double radius, double? start, double? end)
        {
            if (!toWorld.KeepsShape)
            {
                var full = start is null || end is null;
                var sweep = full ? 2 * Math.PI : end!.Value - start!.Value;
                if (sweep <= 0) sweep += 2 * Math.PI;
                var count = ArcPoints * 2;
                result.Polylines.Add(new CadPolyline
                {
                    Style = Style(entity),
                    Closed = full,
                    Points = Enumerable.Range(0, full ? count : count + 1)
                        .Select(k => (start ?? 0) + sweep * k / count)
                        .Select(a => Point(toWorld, cx + radius * Math.Cos(a), cy + radius * Math.Sin(a)))
                        .Select(p => new[] { p.X, p.Y }).ToList(),
                });
                return;
            }
            var (x, y) = Point(toWorld, cx, cy);
            var arc = new CadArc { Style = Style(entity), Cx = x, Cy = y, R = Mm(radius * Math.Sqrt(Math.Abs(toWorld.Det))), IsCircle = start is null || end is null };
            if (start is { } from && end is { } to)
            {
                double Angle(double a)
                {
                    var (dx, dy) = toWorld.Turn(Math.Cos(a), Math.Sin(a));
                    return Degrees(Math.Atan2(dy, dx));
                }
                (arc.StartDeg, arc.EndDeg) = toWorld.Det < 0 ? (Angle(to), Angle(from)) : (Angle(from), Angle(to));
            }
            result.Arcs.Add(arc);
        }

        private void AddText(TextEntity text, Affine toWorld)
        {
            if (text.IsInvisible || string.IsNullOrWhiteSpace(text.Value)) return;
            Count();
            // Left-baseline text sits at its insert point; any other justification at its alignment point.
            var anchor = text.HorizontalAlignment == TextHorizontalAlignment.Left && text.VerticalAlignment == TextVerticalAlignmentType.Baseline
                ? text.InsertPoint
                : text.AlignmentPoint;
            var plane = toWorld * Ocs(text.Normal.Z);
            var (x, y) = Point(plane, anchor.X, anchor.Y);
            var (dx, dy) = plane.Turn(Math.Cos(text.Rotation), Math.Sin(text.Rotation));
            result.Texts.Add(new CadText
            {
                Style = Style(text, withPattern: false), Text = text.Value.Trim(), X = x, Y = y,
                HeightMm = Math.Round(text.Height * scale * Length(plane.Turn(-Math.Sin(text.Rotation), Math.Cos(text.Rotation))), 1),
                RotationDeg = Degrees(Math.Atan2(dy, dx)),
            });
        }

        // Some drawings make grid bubbles and round columns with ellipses of equal axes: those are circles (or arcs),
        // as Revit reports them. A true ellipse becomes a polyline. Unlike arcs and circles, an ellipse is stored in
        // its block's coordinates, its parameter turning about the normal from the major axis.
        private void AddEllipse(Ellipse ellipse, Affine toWorld)
        {
            var major = ellipse.MajorAxisEndPoint;
            var side = ellipse.Normal.Z < 0 ? -1 : 1;
            var sweep = ellipse.EndParameter - ellipse.StartParameter;
            if (sweep <= 0) sweep += 2 * Math.PI;
            var full = Math.Abs(sweep - 2 * Math.PI) < 1e-6;
            (double X, double Y) At(double t) => (
                ellipse.Center.X + major.X * Math.Cos(t) - side * major.Y * ellipse.RadiusRatio * Math.Sin(t),
                ellipse.Center.Y + major.Y * Math.Cos(t) + side * major.X * ellipse.RadiusRatio * Math.Sin(t));

            if (Math.Abs(ellipse.RadiusRatio - 1) < 0.01)
            {
                var (cx, cy) = (ellipse.Center.X, ellipse.Center.Y);
                double? Angle((double X, double Y) p) => full ? null : Math.Atan2(p.Y - cy, p.X - cx);
                // Counterclockwise seen from above: from the start point, or from the end point when the normal points down.
                var (a, b) = side > 0 ? (At(ellipse.StartParameter), At(ellipse.EndParameter)) : (At(ellipse.EndParameter), At(ellipse.StartParameter));
                AddArc(ellipse, toWorld, cx, cy, Math.Sqrt(major.X * major.X + major.Y * major.Y), Angle(a), Angle(b));
                return;
            }
            var count = ArcPoints * 2;
            result.Polylines.Add(new CadPolyline
            {
                Style = Style(ellipse),
                Closed = full,
                Points = Enumerable.Range(0, full ? count : count + 1)
                    .Select(k => At(ellipse.StartParameter + sweep * k / count))
                    .Select(p => Point(toWorld, p.X, p.Y))
                    .Select(p => new[] { p.X, p.Y }).ToList(),
            });
        }

        private static double Length((double X, double Y) v) => Math.Sqrt(v.X * v.X + v.Y * v.Y);

        // Polyline vertices with bulges as points: each bulged segment becomes a few chords.
        private static List<(double X, double Y)> Bulged(IReadOnlyList<(double X, double Y, double Bulge)> vertices, bool closed)
        {
            var points = new List<(double X, double Y)>();
            for (var i = 0; i < vertices.Count; i++)
            {
                var (x0, y0, bulge) = vertices[i];
                points.Add((x0, y0));
                var last = i == vertices.Count - 1;
                if (bulge == 0 || (last && !closed)) continue;
                var (x1, y1, _) = vertices[last ? 0 : i + 1];
                // Bulge = tan(θ/4), θ the included angle, positive counterclockwise.
                var theta = 4 * Math.Atan(bulge);
                var chord = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
                if (chord < 1e-9) continue;
                var radius = chord / (2 * Math.Sin(Math.Abs(theta) / 2));
                var (mx, my) = ((x0 + x1) / 2, (y0 + y1) / 2);
                var sagitta = radius * Math.Cos(theta / 2);
                // Centre: from the chord's middle, to its left for a counterclockwise arc.
                var (nx, ny) = (-(y1 - y0) / chord, (x1 - x0) / chord);
                var (cx, cy) = (mx + nx * sagitta * Math.Sign(theta), my + ny * sagitta * Math.Sign(theta));
                var a0 = Math.Atan2(y0 - cy, x0 - cx);
                for (var k = 1; k < ArcPoints; k++)
                {
                    var a = a0 + theta * k / ArcPoints;
                    points.Add((cx + radius * Math.Cos(a), cy + radius * Math.Sin(a)));
                }
            }
            return points;
        }

        // A boundary loop without repeated points; the closing point is dropped.
        private static List<double[]> Loop(IEnumerable<(double X, double Y)> points)
        {
            var loop = new List<double[]>();
            foreach (var (x, y) in points)
                if (loop.Count == 0 || Math.Abs(loop[^1][0] - x) > 0.01 || Math.Abs(loop[^1][1] - y) > 0.01)
                    loop.Add([x, y]);
            if (loop.Count > 1 && Math.Abs(loop[0][0] - loop[^1][0]) <= 0.01 && Math.Abs(loop[0][1] - loop[^1][1]) <= 0.01)
                loop.RemoveAt(loop.Count - 1);
            return loop;
        }
        // A point in plan, mm.
        private (double X, double Y) Point(Affine toWorld, double x, double y)
        {
            var (wx, wy) = toWorld.Apply(x, y);
            return (Mm(wx), Mm(wy));
        }
        private (double X, double Y) Plan(double x, double y, double normalZ) => (Mm(normalZ < 0 ? -x : x), Mm(y));

        private double Mm(double value) => Math.Round(value * scale, 2);

        private int Style(Entity entity, bool withPattern = true)
        {
            var layer = entity.Layer?.Name ?? "0";
            var pattern = withPattern ? PatternName(entity) : null;
            if (_styles.TryGetValue((layer, pattern), out var index)) return index;
            var off = entity.Layer is { } l && (!l.IsOn || l.Flags.HasFlag(ACadSharp.Tables.LayerFlags.Frozen));
            result.Styles.Add(new CadStyle { Layer = layer, LinePattern = pattern, Color = ColorOf(entity), Category = category, Off = off });
            _styles[(layer, pattern)] = index = result.Styles.Count - 1;
            return index;
        }

        // The line type the entity draws with, "Solid" for continuous lines as Revit reports them.
        private static string? PatternName(Entity entity)
        {
            try
            {
                var name = entity.GetActiveLineType()?.Name;
                return name is null ? null : name.Equals("Continuous", StringComparison.OrdinalIgnoreCase) ? "Solid" : name;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string? ColorOf(Entity entity)
        {
            try
            {
                var color = entity.GetActiveColor();
                return color.IsByLayer || color.IsByBlock ? null : $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static double Degrees(double radians)
        {
            var degrees = radians * 180 / Math.PI % 360;
            return Math.Round(degrees < 0 ? degrees + 360 : degrees, 4);
        }

        private void Count() => _entities++;

        private void Skip(string kind) => result.Skipped[kind] = result.Skipped.GetValueOrDefault(kind) + 1;
    }

    // A plane's affine map: x' = A·x + C·y + E, y' = B·x + D·y + F. In m * n, n applies first.
    private readonly record struct Affine(double A, double B, double C, double D, double E, double F)
    {
        public static readonly Affine Identity = new(1, 0, 0, 1, 0, 0);

        public static Affine Move(double x, double y) => new(1, 0, 0, 1, x, y);

        public static Affine Rotate(double radians) => new(Math.Cos(radians), Math.Sin(radians), -Math.Sin(radians), Math.Cos(radians), 0, 0);

        public static Affine Scale(double x, double y) => new(x, 0, 0, y, 0, 0);

        public static Affine operator *(Affine m, Affine n) => new(
            m.A * n.A + m.C * n.B, m.B * n.A + m.D * n.B,
            m.A * n.C + m.C * n.D, m.B * n.C + m.D * n.D,
            m.A * n.E + m.C * n.F + m.E, m.B * n.E + m.D * n.F + m.F);

        public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);

        // A direction: the map without its move.
        public (double X, double Y) Turn(double x, double y) => (A * x + C * y, B * x + D * y);

        public double Det => A * D - B * C;

        // Circles stay circles: both axes as long, and square to each other.
        public bool KeepsShape
        {
            get
            {
                var (xx, yy) = (A * A + B * B, C * C + D * D);
                var size = Math.Max(xx, yy);
                return Math.Abs(xx - yy) <= 1e-9 * size && Math.Abs(A * C + B * D) <= 1e-9 * size;
            }
        }
    }
}
