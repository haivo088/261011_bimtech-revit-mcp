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
        foreach (var entity in document.ModelSpace.Entities) builder.Add(entity, 0);
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

    private sealed class Builder(CadCase result, double scale, string category, int maxEntities)
    {
        private readonly Dictionary<(string Layer, string? Pattern), int> _styles = [];
        private int _entities;

        public void Add(Entity entity, int depth)
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
                    // Attribute values are real texts (grid names in bubbles, marks); the block's attribute
                    // definitions are only their templates.
                    // A nested block's attributes come out of the outer block's Explode placed by their insert point only.
                    foreach (var attribute in insert.Attributes) AddText(attribute, exploded: depth > 0);
                    if (depth >= MaxBlockDepth)
                    {
                        Skip("block nested too deep");
                        return;
                    }
                    foreach (var part in insert.Explode()) Add(part, depth + 1);
                    return;
                case AttributeDefinition:
                    return;
                case Line line:
                    Count();
                    result.Lines.Add(new CadLine
                    {
                        Style = Style(entity),
                        X0 = Mm(line.StartPoint.X), Y0 = Mm(line.StartPoint.Y),
                        X1 = Mm(line.EndPoint.X), Y1 = Mm(line.EndPoint.Y),
                    });
                    return;
                case Arc arc:
                    Count();
                    result.Arcs.Add(ToArc(arc, entity));
                    return;
                case Circle circle:
                    Count();
                    var (cx, cy) = Plan(circle.Center.X, circle.Center.Y, circle.Normal.Z);
                    result.Arcs.Add(new CadArc { Style = Style(entity), Cx = cx, Cy = cy, R = Mm(circle.Radius), IsCircle = true });
                    return;
                case Ellipse ellipse:
                    Count();
                    AddEllipse(ellipse);
                    return;
                case LwPolyline polyline:
                    Count();
                    result.Polylines.Add(new CadPolyline
                    {
                        Style = Style(entity),
                        Closed = polyline.IsClosed,
                        Points = Bulged(polyline.Vertices.Select(v => (v.Location.X, v.Location.Y, v.Bulge)).ToList(), polyline.IsClosed)
                            .Select(p => Plan(p.X, p.Y, polyline.Normal.Z)).Select(p => new[] { p.X, p.Y }).ToList(),
                    });
                    return;
                case Hatch hatch:
                    Count();
                    var loops = hatch.Paths
                        .Select(path => Loop(path.GetPoints(ArcPoints).Select(p => Plan(p.X, p.Y, hatch.Normal.Z))))
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
                    AddText(text, exploded: depth > 0);
                    return;
                case MText text:
                    Count();
                    var value = string.Join(" ", text.GetPlainTextLines().Select(line => line.Trim()).Where(line => line.Length > 0));
                    if (value.Length == 0) return;
                    var (tx, ty) = Plan(text.InsertPoint.X, text.InsertPoint.Y, text.Normal.Z);
                    result.Texts.Add(new CadText
                    {
                        Style = Style(entity, withPattern: false), Text = value, X = tx, Y = ty,
                        HeightMm = Math.Round(text.Height * scale, 1), RotationDeg = Degrees(text.Rotation),
                    });
                    return;
                default:
                    Skip(entity.GetType().Name);
                    return;
            }
        }

        private void AddText(TextEntity text, bool exploded)
        {
            if (text.IsInvisible || string.IsNullOrWhiteSpace(text.Value)) return;
            Count();
            // Left-baseline text sits at its insert point; any other justification at its alignment point. Explode
            // (ACadSharp) moves a text's insert point but not its alignment point, so a text from a block (grid
            // bubbles, marks) is placed by its insert point, within about one text height of its alignment point.
            var anchor = exploded
                || text.HorizontalAlignment == TextHorizontalAlignment.Left && text.VerticalAlignment == TextVerticalAlignmentType.Baseline
                ? text.InsertPoint
                : text.AlignmentPoint;
            var (x, y) = Plan(anchor.X, anchor.Y, text.Normal.Z);
            result.Texts.Add(new CadText
            {
                Style = Style(text, withPattern: false), Text = text.Value.Trim(), X = x, Y = y,
                HeightMm = Math.Round(text.Height * scale, 1), RotationDeg = Degrees(text.Rotation),
            });
        }

        // Some drawings make grid bubbles and round columns with ellipses of equal axes: those are circles (or arcs),
        // as Revit reports them. A true ellipse becomes a polyline. Unlike arcs and circles, an ellipse is stored in
        // world coordinates, its parameter turning about the normal from the major axis.
        private void AddEllipse(Ellipse ellipse)
        {
            var major = ellipse.MajorAxisEndPoint;
            var side = ellipse.Normal.Z < 0 ? -1 : 1;
            var sweep = ellipse.EndParameter - ellipse.StartParameter;
            if (sweep <= 0) sweep += 2 * Math.PI;
            var full = Math.Abs(sweep - 2 * Math.PI) < 1e-6;
            (double X, double Y) At(double t) => (
                Mm(ellipse.Center.X + major.X * Math.Cos(t) - side * major.Y * ellipse.RadiusRatio * Math.Sin(t)),
                Mm(ellipse.Center.Y + major.Y * Math.Cos(t) + side * major.X * ellipse.RadiusRatio * Math.Sin(t)));

            if (Math.Abs(ellipse.RadiusRatio - 1) < 0.01)
            {
                var arc = new CadArc
                {
                    Style = Style(ellipse), Cx = Mm(ellipse.Center.X), Cy = Mm(ellipse.Center.Y),
                    R = Mm(Math.Sqrt(major.X * major.X + major.Y * major.Y)), IsCircle = full,
                };
                if (!full)
                {
                    // Counterclockwise seen from above: from the start point, or from the end point when the normal points down.
                    var (a, b) = side > 0 ? (At(ellipse.StartParameter), At(ellipse.EndParameter)) : (At(ellipse.EndParameter), At(ellipse.StartParameter));
                    arc.StartDeg = Degrees(Math.Atan2(a.Y - arc.Cy, a.X - arc.Cx));
                    arc.EndDeg = Degrees(Math.Atan2(b.Y - arc.Cy, b.X - arc.Cx));
                }
                result.Arcs.Add(arc);
                return;
            }
            var count = ArcPoints * 2;
            result.Polylines.Add(new CadPolyline
            {
                Style = Style(ellipse),
                Closed = full,
                Points = Enumerable.Range(0, full ? count : count + 1)
                    .Select(k => At(ellipse.StartParameter + sweep * k / count))
                    .Select(p => new[] { p.X, p.Y }).ToList(),
            });
        }

        private CadArc ToArc(Arc arc, Entity entity)
        {
            var (cx, cy) = Plan(arc.Center.X, arc.Center.Y, arc.Normal.Z);
            var (start, end) = (arc.StartAngle, arc.EndAngle);
            // Seen from below (normal down), counterclockwise there is clockwise from above, and x is mirrored.
            if (arc.Normal.Z < 0) (start, end) = (Math.PI - end, Math.PI - start);
            return new CadArc { Style = Style(entity), Cx = cx, Cy = cy, R = Mm(arc.Radius), StartDeg = Degrees(start), EndDeg = Degrees(end) };
        }

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

        // A 2D entity's point in plan, mm. Entities drawn with their normal pointing down (mirrored in AutoCAD)
        // keep coordinates in a system whose x runs the other way.
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
}
