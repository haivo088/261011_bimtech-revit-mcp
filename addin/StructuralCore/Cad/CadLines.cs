using Autodesk.Revit.DB;
using View = Autodesk.Revit.DB.View;

namespace Bimtech.StructuralCore.Cad;

/// <summary>Mọi đoạn thẳng (Line và từng cạnh PolyLine) của 1 CAD như hiển thị trong view, kèm layer, nét đứt hay
/// không và tên line pattern (theo layer), Z = 0 — kể cả trong block lồng nhau. Cung tròn bỏ qua.</summary>
internal static class CadLines
{
    private const int MaxRecursionDepth = 8;

    public static List<(string Layer, XYZ A, XYZ B, bool Dashed, string? Pattern)> Read(Document doc, ImportInstance cad, View view, IReadOnlyCollection<string>? layers = null)
    {
        var result = new List<(string, XYZ, XYZ, bool, string?)>();
        GeometryElement? geometry;
        try
        {
            geometry = cad.get_Geometry(new Options { View = view, IncludeNonVisibleObjects = false });
        }
        catch (Exception)
        {
            return result;
        }
        var patterns = new Dictionary<ElementId, (bool Dashed, string? Name)>();
        if (geometry is not null) Walk(doc, geometry, layers, result, patterns, 0);
        return result;
    }

    private static void Walk(
        Document doc, GeometryElement geometry, IReadOnlyCollection<string>? layers, List<(string, XYZ, XYZ, bool, string?)> result,
        Dictionary<ElementId, (bool Dashed, string? Name)> dashed, int depth)
    {
        if (depth > MaxRecursionDepth) return;
        foreach (var obj in geometry)
        {
            try
            {
                switch (obj)
                {
                    case GeometryInstance instance when instance.GetInstanceGeometry() is { } nested:
                        Walk(doc, nested, layers, result, dashed, depth + 1);
                        break;
                    case Line line:
                        Add(doc, line.GraphicsStyleId, line.GetEndPoint(0), line.GetEndPoint(1), layers, result, dashed);
                        break;
                    case PolyLine polyLine:
                        var points = polyLine.GetCoordinates();
                        for (var i = 0; i + 1 < points.Count; i++)
                            Add(doc, polyLine.GraphicsStyleId, points[i], points[i + 1], layers, result, dashed);
                        break;
                }
            }
            catch (Exception)
            {
                // Bỏ qua riêng 1 đối tượng lỗi, không sập cả lượt đọc.
            }
        }
    }

    private static void Add(
        Document doc, ElementId styleId, XYZ a, XYZ b, IReadOnlyCollection<string>? layers, List<(string, XYZ, XYZ, bool, string?)> result,
        Dictionary<ElementId, (bool Dashed, string? Name)> patterns)
    {
        var layer = CadGeometry.ResolveLayerName(doc, styleId);
        if (layers is { Count: > 0 } && !layers.Contains(layer)) return;
        var (flatA, flatB) = (new XYZ(a.X, a.Y, 0), new XYZ(b.X, b.Y, 0));
        if (flatA.DistanceTo(flatB) <= 1e-9) return;
        if (!patterns.TryGetValue(styleId, out var pattern)) patterns[styleId] = pattern = Pattern(doc, styleId);
        result.Add((layer, flatA, flatB, pattern.Dashed, pattern.Name));
    }

    /// <summary>Line pattern của layer: nét đứt (nét khuất, nét tâm...) khi khác nét liền, và tên của nó.</summary>
    private static (bool Dashed, string? Name) Pattern(Document doc, ElementId styleId)
    {
        if (doc.GetElement(styleId) is not GraphicsStyle { GraphicsStyleCategory: { } category }) return (false, null);
        var pattern = category.GetLinePatternId(GraphicsStyleType.Projection);
        if (pattern == ElementId.InvalidElementId || pattern == LinePatternElement.GetSolidPatternId()) return (false, null);
        return (true, doc.GetElement(pattern)?.Name);
    }
}
