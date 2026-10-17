using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands;
using Bimtech.RevitMcp.Recognition;

namespace Bimtech.RevitMcp.Cad;

// Reads one CAD import or link into a CadCase: every line, arc, circle and polyline in model coordinates with
// its graphics style (layer, line pattern, color), plus the Text Notes of the view. Read-only.
internal static class CadExtractor
{
    private const int MaxDepth = 8;

    internal sealed record Limits(int MaxEntities);

    public static CadCase Extract(Document document, ImportInstance cad, View? view, Limits limits)
    {
        var type = document.GetElement(cad.GetTypeId());
        var result = new CadCase
        {
            CreatedAt = DateTimeOffset.Now,
            Source = new CadCaseSource
            {
                Document = document.Title,
                CadName = type?.Name ?? cad.Name,
                CadId = RevitJson.Id(cad.Id),
                IsLinked = cad.IsLinked,
                View = view?.Name,
                Level = (view as ViewPlan)?.GenLevel?.Name,
            },
        };

        var walker = new Walker(document, result, limits.MaxEntities);
        var options = view is null ? new Options() : new Options { View = view };
        if (cad.get_Geometry(options) is { } geometry) walker.Walk(geometry, 0);

        if (view is not null)
            foreach (var note in new FilteredElementCollector(document, view.Id).OfClass(typeof(TextNote)).Cast<TextNote>())
                result.Texts.Add(Text(document, note, view));
        return result;
    }

    // The columns and walls whose height reaches the elevation (feet), as beam supports: a column's outline is the
    // convex hull of its solids in plan (its real shape, whatever the family calls its parameters), a wall's the
    // rectangle of its centre line and width. Columns above and below the level at the same spot count once (the
    // one ending at the level). Used by propose_beams and saved in cases by export_cad_case.
    public static List<CaseSupport> ModelSupports(Document document, double elevation)
    {
        var tolerance = UnitUtils.ConvertToInternalUnits(50, UnitTypeId.Millimeters);
        bool Reaches(Element element) =>
            element.get_BoundingBox(null) is { } box && box.Min.Z - tolerance <= elevation && box.Max.Z + tolerance >= elevation;

        var supports = new List<CaseSupport>();
        var columns = new FilteredElementCollector(document).OfCategory(BuiltInCategory.OST_StructuralColumns)
            .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
            .Where(Reaches)
            .OrderBy(column => Math.Abs(column.get_BoundingBox(null)!.Max.Z - elevation));
        foreach (var column in columns)
        {
            if (Outline(column, "column") is not { } support) continue;
            if (supports.Any(other => Math.Abs(other.Cx - support.Cx) <= 50 && Math.Abs(other.Cy - support.Cy) <= 50)) continue;
            supports.Add(support);
        }

        supports.AddRange(new FilteredElementCollector(document).OfClass(typeof(Wall)).Cast<Wall>().Where(Reaches)
            .Select(WallSupport).OfType<CaseSupport>());
        return supports;
    }

    // What a column of the storey starting at the elevation (feet) stands on: columns ending there, and pile caps or
    // footings whose top is at most 2.5 m below it (caps sit under the ground floor).
    public static List<CaseSupport> ModelSupportsBelow(Document document, double elevation)
    {
        var tolerance = UnitUtils.ConvertToInternalUnits(100, UnitTypeId.Millimeters);
        var depth = UnitUtils.ConvertToInternalUnits(2500, UnitTypeId.Millimeters);
        var columns = new FilteredElementCollector(document).OfCategory(BuiltInCategory.OST_StructuralColumns)
            .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
            .Where(column => column.get_BoundingBox(null) is { } box
                && Math.Abs(box.Max.Z - elevation) <= tolerance && box.Min.Z < elevation - tolerance)
            .Select(column => Outline(column, "column"));
        var caps = new FilteredElementCollector(document).OfCategory(BuiltInCategory.OST_StructuralFoundation)
            .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
            .Where(cap => cap.get_BoundingBox(null) is { } box && box.Max.Z <= elevation + tolerance && box.Max.Z >= elevation - depth)
            .Select(cap => Outline(cap, "cap"));
        return columns.Concat(caps).OfType<CaseSupport>().ToList();
    }

    // An element's plan outline: the convex hull of its solids (its real shape, whatever its family calls its
    // parameters), centred on its insertion point.
    private static CaseSupport? Outline(FamilyInstance element, string kind)
    {
        var hull = Bimtech.StructuralCore.Cad.CadGeometry.ConvexHull(SolidPoints(element));
        if (hull.Count < 3) return null;
        var center = element.Location is LocationPoint location
            ? location.Point
            : new XYZ(hull.Average(p => p.X), hull.Average(p => p.Y), 0);
        return new CaseSupport
        {
            Kind = kind, Id = RevitJson.Id(element.Id), Mark = Mark(element), Cx = Mm(center.X), Cy = Mm(center.Y),
            Points = hull.Select(p => new[] { Mm(p.X), Mm(p.Y) }).ToList(),
        };
    }

    // The straight walls whose top is at the elevation (feet), i.e. the walls of the storey below it.
    public static List<CaseSupport> ModelWallsBelow(Document document, double elevation)
    {
        var tolerance = UnitUtils.ConvertToInternalUnits(100, UnitTypeId.Millimeters);
        return new FilteredElementCollector(document).OfClass(typeof(Wall)).Cast<Wall>()
            .Where(wall => wall.get_BoundingBox(null) is { } box
                && Math.Abs(box.Max.Z - elevation) <= tolerance && box.Min.Z < elevation - tolerance)
            .Select(WallSupport).OfType<CaseSupport>()
            .ToList();
    }

    // A straight wall as the rectangle of its centre line and width: points 0-1 on one face, 2-3 on the other.
    private static CaseSupport? WallSupport(Wall wall)
    {
        if (wall.Location is not LocationCurve { Curve: Line line }) return null;
        var (a, b) = (line.GetEndPoint(0), line.GetEndPoint(1));
        var normal = new XYZ(-(b - a).Y, (b - a).X, 0).Normalize() * (wall.Width / 2);
        return new CaseSupport
        {
            Kind = "wall", Id = RevitJson.Id(wall.Id), Mark = Mark(wall),
            Cx = Mm((a.X + b.X) / 2), Cy = Mm((a.Y + b.Y) / 2),
            Points = new[] { a + normal, b + normal, b - normal, a - normal }.Select(p => new[] { Mm(p.X), Mm(p.Y) }).ToList(),
        };
    }

    // The beams placed on the level, as the person approved them: the truth a case's beams are checked against.
    public static List<TruthBeam> ModelBeams(Document document, Level level) =>
        LevelBeams(document, level).Select(beam => beam.Truth).ToList();

    // The beams placed on the level as supports of secondary beams: each the rectangle of its axis and width b.
    public static List<CaseSupport> ModelBeamSupports(Document document, Level level) =>
        LevelBeams(document, level).Select(beam => BeamCase.BeamSupport(beam.Truth, RevitJson.Id(beam.Id))).ToList();

    private static IEnumerable<(ElementId Id, TruthBeam Truth)> LevelBeams(Document document, Level level) =>
        new FilteredElementCollector(document).OfCategory(BuiltInCategory.OST_StructuralFraming)
            .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
            .Where(beam => beam.StructuralType == Autodesk.Revit.DB.Structure.StructuralType.Beam
                && beam.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM)?.AsElementId() == level.Id
                && beam.Location is LocationCurve { Curve: Line })
            .Select(beam =>
            {
                var line = (Line)((LocationCurve)beam.Location).Curve;
                var (start, end) = (line.GetEndPoint(0), line.GetEndPoint(1));
                return (beam.Id, new TruthBeam
                {
                    Mark = Mark(beam), X0 = Mm(start.X), Y0 = Mm(start.Y), X1 = Mm(end.X), Y1 = Mm(end.Y),
                    BMm = TypeLength(beam.Symbol, "b"), HMm = TypeLength(beam.Symbol, "h"),
                });
            });

    private static string? Mark(Element element) =>
        element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() is { Length: > 0 } mark ? mark : null;

    private static double? TypeLength(FamilySymbol symbol, string name) =>
        symbol.LookupParameter(name) is { StorageType: StorageType.Double } parameter ? Mm(parameter.AsDouble()) : null;

    // The corners of an element's solids, flattened to plan.
    private static List<XYZ> SolidPoints(Element element)
    {
        var points = new List<XYZ>();
        void Walk(GeometryElement geometry)
        {
            foreach (var obj in geometry)
            {
                if (obj is Solid { Volume: > 0 } solid)
                    foreach (Edge edge in solid.Edges)
                        points.AddRange(edge.Tessellate().Select(p => new XYZ(p.X, p.Y, 0)));
                else if (obj is GeometryInstance instance && instance.GetInstanceGeometry() is { } nested)
                    Walk(nested);
            }
        }
        if (element.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Coarse }) is { } geometry) Walk(geometry);
        return points;
    }

    // The grids in the model, as the person approved them: the truth a case is checked against.
    public static List<TruthGrid> ModelGrids(Document document) =>
        new FilteredElementCollector(document).OfClass(typeof(Grid)).Cast<Grid>()
            .Where(grid => grid.Curve is Line)
            .Select(grid =>
            {
                var line = (Line)grid.Curve;
                var (start, end) = (line.GetEndPoint(0), line.GetEndPoint(1));
                return new TruthGrid { Name = grid.Name, X0 = Mm(start.X), Y0 = Mm(start.Y), X1 = Mm(end.X), Y1 = Mm(end.Y) };
            })
            .OrderBy(grid => grid.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private sealed class Walker(Document document, CadCase result, int maxEntities)
    {
        private readonly Dictionary<ElementId, int> _styles = [];
        private int _entities;

        public void Walk(GeometryElement geometry, int depth)
        {
            if (depth > MaxDepth) return;
            foreach (var item in geometry)
            {
                if (_entities >= maxEntities)
                {
                    Skip("over maxEntities");
                    continue;
                }
                switch (item)
                {
                    case GeometryInstance instance:
                        if (instance.GetInstanceGeometry() is { } nested) Walk(nested, depth + 1);
                        break;
                    case Line line:
                        _entities++;
                        result.Lines.Add(new CadLine
                        {
                            Style = Style(line.GraphicsStyleId),
                            X0 = Mm(line.GetEndPoint(0).X), Y0 = Mm(line.GetEndPoint(0).Y),
                            X1 = Mm(line.GetEndPoint(1).X), Y1 = Mm(line.GetEndPoint(1).Y),
                        });
                        break;
                    case Arc arc:
                        _entities++;
                        result.Arcs.Add(ToArc(arc));
                        break;
                    case PolyLine polyline:
                        _entities++;
                        result.Polylines.Add(ToPolyline(polyline));
                        break;
                    default:
                        Skip(item.GetType().Name);
                        break;
                }
            }
        }

        private CadArc ToArc(Arc arc)
        {
            var center = arc.Center;
            var value = new CadArc { Style = Style(arc.GraphicsStyleId), Cx = Mm(center.X), Cy = Mm(center.Y), R = Mm(arc.Radius) };
            if (!arc.IsBound)
            {
                value.IsCircle = true;
                return value;
            }
            // Counterclockwise seen from above: a clockwise arc (normal pointing down) runs from its end to its start.
            var (from, to) = arc.Normal.Z >= 0 ? (arc.GetEndPoint(0), arc.GetEndPoint(1)) : (arc.GetEndPoint(1), arc.GetEndPoint(0));
            value.StartDeg = Degrees(from - center);
            value.EndDeg = Degrees(to - center);
            return value;
        }

        private CadPolyline ToPolyline(PolyLine polyline)
        {
            var points = polyline.GetCoordinates().Select(point => new[] { Mm(point.X), Mm(point.Y) }).ToList();
            var closed = points.Count > 2 && Math.Abs(points[0][0] - points[^1][0]) < 0.01 && Math.Abs(points[0][1] - points[^1][1]) < 0.01;
            if (closed) points.RemoveAt(points.Count - 1);
            return new CadPolyline { Style = Style(polyline.GraphicsStyleId), Closed = closed, Points = points };
        }

        // Index of the style in result.Styles, adding it on first use.
        private int Style(ElementId id)
        {
            if (_styles.TryGetValue(id, out var index)) return index;
            var category = (document.GetElement(id) as GraphicsStyle)?.GraphicsStyleCategory;
            result.Styles.Add(new CadStyle
            {
                Layer = category?.Name ?? "",
                LinePattern = category is null ? null : PatternName(category),
                Color = category?.LineColor is { IsValid: true } color ? $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}" : null,
                Category = category?.Parent?.Name,
            });
            _styles[id] = index = result.Styles.Count - 1;
            return index;
        }

        private string? PatternName(Category category)
        {
            var patternId = category.GetLinePatternId(GraphicsStyleType.Projection);
            if (patternId == LinePatternElement.GetSolidPatternId()) return "Solid";
            return (document.GetElement(patternId) as LinePatternElement)?.Name;
        }

        private void Skip(string kind) => result.Skipped[kind] = result.Skipped.GetValueOrDefault(kind) + 1;
    }

    private static CadText Text(Document document, TextNote note, View view)
    {
        var size = (document.GetElement(note.GetTypeId()) as TextNoteType)?.get_Parameter(BuiltInParameter.TEXT_SIZE)?.AsDouble() ?? 0;
        return new CadText
        {
            Text = note.Text.Trim(),
            X = Mm(note.Coord.X),
            Y = Mm(note.Coord.Y),
            // Text size is on paper; on the model it is that times the view scale.
            HeightMm = Math.Round(Mm(size) * Math.Max(1, view.Scale), 1),
            RotationDeg = Math.Round(Degrees(note.BaseDirection), 2),
        };
    }

    private static double Degrees(XYZ vector)
    {
        var degrees = Math.Atan2(vector.Y, vector.X) * 180 / Math.PI;
        return Math.Round(degrees < 0 ? degrees + 360 : degrees, 4);
    }

    private static double Mm(double feet) => Math.Round(UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters), 2);
}
