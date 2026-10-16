using Autodesk.Revit.DB;
using System.Globalization;
using System.Text;
using View = Autodesk.Revit.DB.View;

namespace Bimtech.StructuralCore.Levels;

/// <summary>Tạo hệ Grid trực giao đơn giản từ layer CAD có tên gợi ý Grid/Trục/Axis.
/// Plan chỉ đọc model và trả về đúng các Grid mà Create sẽ dựng (dùng cho xem trước/duyệt).</summary>
internal static class GridBuilder
{
    internal sealed record BuildResult(int GridCount, XYZ AlignmentTranslation);

    /// <summary>1 Grid sẽ tạo: tên (1, 2, … hoặc A, B, …) và đường thẳng ở toạ độ SAU khi dời CAD.</summary>
    internal sealed record PlannedGrid(string Name, Line Line, bool IsVertical);

    /// <summary>Kế hoạch dựng Grid: layer nguồn, vector dời CAD để giao điểm 1-A trùng Project Base Point, các Grid
    /// sẽ tạo, và tên Grid đã có trong project (Create sẽ dừng khi gặp tên trùng).</summary>
    internal sealed record GridPlan(string SourceLayer, XYZ AlignmentTranslation, List<PlannedGrid> Grids, List<string> ExistingNames);

    private static readonly double MinLength = UnitUtils.ConvertToInternalUnits(1000, UnitTypeId.Millimeters);
    private static readonly double CoordinateTolerance = UnitUtils.ConvertToInternalUnits(10, UnitTypeId.Millimeters);
    private static readonly double Padding = UnitUtils.ConvertToInternalUnits(1000, UnitTypeId.Millimeters);
    private const double AxisTolerance = 0.01;

    private sealed record Segment(XYZ P0, XYZ P1)
    {
        public double Length => P0.DistanceTo(P1);
        public bool Vertical => Math.Abs(P1.Y - P0.Y) >= Math.Abs(P1.X - P0.X);
        public double Coordinate => Vertical ? (P0.X + P1.X) / 2 : (P0.Y + P1.Y) / 2;
    }

    /// <summary>Tính các Grid sẽ dựng từ CAD trong view, không sửa model. Ném InvalidOperationException khi không có
    /// layer Grid hoặc layer thiếu trục đứng/ngang — cùng thông báo với Create.</summary>
    public static GridPlan Plan(Document doc, ViewPlan view, ImportInstance cad)
    {
        var layers = ReadLayers(doc, view, cad);
        var source = layers
            .Where(x => IsGridLayer(x.Key))
            .OrderByDescending(x => x.Value.Sum(s => s.Length))
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(source.Key))
            throw new InvalidOperationException(
                "Không tìm thấy layer Grid. Tên layer cần chứa GRID, TRUC, TRỤC hoặc AXIS.");

        var vertical = MergeByCoordinate(source.Value.Where(IsVertical));
        var horizontal = MergeByCoordinate(source.Value.Where(IsHorizontal));
        if (vertical.Count == 0 || horizontal.Count == 0)
            throw new InvalidOperationException($"Layer '{source.Key}' cần có cả trục đứng và trục ngang.");

        var basePoint = BasePoint.GetProjectBasePoint(doc).Position;
        var axis1Coordinate = vertical.Min(s => s.Coordinate);
        var axisACoordinate = horizontal.Min(s => s.Coordinate);
        var dx = basePoint.X - axis1Coordinate;
        var dy = basePoint.Y - axisACoordinate;
        var translation = new XYZ(dx, dy, 0);

        var minX = horizontal.Min(s => Math.Min(s.P0.X, s.P1.X)) + dx - Padding;
        var maxX = horizontal.Max(s => Math.Max(s.P0.X, s.P1.X)) + dx + Padding;
        var minY = vertical.Min(s => Math.Min(s.P0.Y, s.P1.Y)) + dy - Padding;
        var maxY = vertical.Max(s => Math.Max(s.P0.Y, s.P1.Y)) + dy + Padding;
        var grids = new List<PlannedGrid>();

        foreach (var item in vertical.OrderBy(s => s.Coordinate).Select((s, i) => (Segment: s, Name: (i + 1).ToString())))
        {
            // Trục 1 là mốc tuyệt đối; mọi khoảng cách còn lại làm tròn theo mm nguyên để Dim sạch.
            var x = basePoint.X + SnapOffsetToMillimeter(item.Segment.Coordinate - axis1Coordinate);
            grids.Add(new PlannedGrid(item.Name, Line.CreateBound(new XYZ(x, minY, 0), new XYZ(x, maxY, 0)), true));
        }

        foreach (var item in horizontal.OrderBy(s => s.Coordinate).Select((s, i) => (Segment: s, Name: ToLetters(i))))
        {
            // Trục A là mốc tuyệt đối; mọi khoảng cách còn lại làm tròn theo mm nguyên để Dim sạch.
            var y = basePoint.Y + SnapOffsetToMillimeter(item.Segment.Coordinate - axisACoordinate);
            grids.Add(new PlannedGrid(item.Name, Line.CreateBound(new XYZ(minX, y, 0), new XYZ(maxX, y, 0)), false));
        }

        var existing = ExistingGridNames(doc);
        var conflicts = grids.Select(g => g.Name).Where(existing.Contains).ToList();
        return new GridPlan(source.Key, translation, grids, conflicts);
    }

    /// <summary>Dời CAD theo kế hoạch rồi dựng Grid. Gọi trong 1 Transaction đang mở. Dừng (ném lỗi) ở Grid đầu tiên
    /// trùng tên với Grid đã có — caller rollback Transaction như trước khi tách khỏi tool.</summary>
    public static BuildResult Create(Document doc, ViewPlan view, ImportInstance cad)
    {
        var plan = Plan(doc, view, cad);

        MoveCad(doc, cad, plan.AlignmentTranslation);
        doc.Regenerate();

        foreach (var grid in plan.Grids)
            CreateNamedGrid(doc, grid.Line, grid.Name);

        return new BuildResult(plan.Grids.Count, plan.AlignmentTranslation);
    }

    public static void MoveCad(Document doc, ImportInstance cad, XYZ translation)
    {
        if (translation.GetLength() <= 1e-9) return;
        var wasPinned = cad.Pinned;
        if (wasPinned) cad.Pinned = false;
        ElementTransformUtils.MoveElement(doc, cad.Id, translation);
        if (wasPinned) cad.Pinned = true;
    }

    private static HashSet<string> ExistingGridNames(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>()
            .Select(g => g.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static void CreateNamedGrid(Document doc, Line line, string name)
    {
        if (new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>()
            .Any(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Project đã có Grid tên '{name}'.");

        var grid = Grid.Create(doc, line);
        grid.Name = name;
    }

    private static List<Segment> MergeByCoordinate(IEnumerable<Segment> source)
    {
        var result = new List<Segment>();
        foreach (var group in source.OrderBy(s => s.Coordinate).Aggregate(
                     new List<List<Segment>>(), (groups, segment) =>
                     {
                         if (groups.Count == 0 || Math.Abs(groups[^1].Average(x => x.Coordinate) - segment.Coordinate) > CoordinateTolerance)
                             groups.Add([]);
                         groups[^1].Add(segment);
                         return groups;
                     }))
        {
            var representative = group.OrderByDescending(s => s.Length).First();
            var vertical = representative.Vertical;
            // Không lấy trung bình: giữ tọa độ của nét dài nhất để tránh Grid bị trôi khỏi nét CAD chính.
            var coordinate = representative.Coordinate;
            var min = group.Min(s => vertical ? Math.Min(s.P0.Y, s.P1.Y) : Math.Min(s.P0.X, s.P1.X));
            var max = group.Max(s => vertical ? Math.Max(s.P0.Y, s.P1.Y) : Math.Max(s.P0.X, s.P1.X));
            result.Add(vertical
                ? new Segment(new XYZ(coordinate, min, 0), new XYZ(coordinate, max, 0))
                : new Segment(new XYZ(min, coordinate, 0), new XYZ(max, coordinate, 0)));
        }
        return result;
    }

    private static double SnapOffsetToMillimeter(double offsetFeet)
    {
        var offsetMm = UnitUtils.ConvertFromInternalUnits(offsetFeet, UnitTypeId.Millimeters);
        return UnitUtils.ConvertToInternalUnits(Math.Round(offsetMm, MidpointRounding.AwayFromZero), UnitTypeId.Millimeters);
    }

    private static bool IsVertical(Segment s) => s.Length >= MinLength &&
        Math.Abs(s.P1.X - s.P0.X) / s.Length <= AxisTolerance;

    private static bool IsHorizontal(Segment s) => s.Length >= MinLength &&
        Math.Abs(s.P1.Y - s.P0.Y) / s.Length <= AxisTolerance;

    private static Dictionary<string, List<Segment>> ReadLayers(Document doc, Autodesk.Revit.DB.View view, ImportInstance cad)
    {
        var result = new Dictionary<string, List<Segment>>(StringComparer.OrdinalIgnoreCase);
        var geometry = cad.get_Geometry(new Options { View = view, IncludeNonVisibleObjects = false });
        if (geometry != null) Walk(doc, geometry, result, 0);
        return result;
    }

    private static void Walk(Document doc, GeometryElement geometry, Dictionary<string, List<Segment>> result, int depth)
    {
        if (depth > 8) return;
        foreach (var obj in geometry)
        {
            if (obj is GeometryInstance instance)
            {
                var nested = instance.GetInstanceGeometry();
                if (nested != null) Walk(doc, nested, result, depth + 1);
                continue;
            }
            if (obj is Line line)
                Add(doc, result, line.GraphicsStyleId, line.GetEndPoint(0), line.GetEndPoint(1));
            else if (obj is PolyLine polyline)
            {
                var points = polyline.GetCoordinates();
                for (var i = 0; i < points.Count - 1; i++) Add(doc, result, polyline.GraphicsStyleId, points[i], points[i + 1]);
            }
        }
    }

    private static void Add(Document doc, Dictionary<string, List<Segment>> result, ElementId styleId, XYZ p0, XYZ p1)
    {
        if (doc.GetElement(styleId) is not GraphicsStyle style) return;
        var layer = style.GraphicsStyleCategory?.Name;
        if (string.IsNullOrWhiteSpace(layer)) return;
        var segment = new Segment(new XYZ(p0.X, p0.Y, 0), new XYZ(p1.X, p1.Y, 0));
        if (segment.Length < MinLength) return;
        if (!result.TryGetValue(layer, out var list)) result[layer] = list = [];
        list.Add(segment);
    }

    private static bool IsGridLayer(string name)
    {
        var normalized = RemoveDiacritics(name).ToUpperInvariant();
        return normalized.Contains("GRID") || normalized.Contains("TRUC") || normalized.Contains("AXIS");
    }

    private static string RemoveDiacritics(string value)
    {
        var text = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder();
        foreach (var c in text)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(c);
        return builder.ToString().Normalize(NormalizationForm.FormC).Replace('Đ', 'D').Replace('đ', 'd');
    }

    private static string ToLetters(int index)
    {
        var value = index + 1;
        var result = string.Empty;
        while (value > 0)
        {
            value--;
            result = (char)('A' + value % 26) + result;
            value /= 26;
        }
        return result;
    }
}
