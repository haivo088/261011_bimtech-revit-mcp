using Autodesk.Revit.DB;
using View = Autodesk.Revit.DB.View;

namespace Bimtech.StructuralCore.Cad;

/// <summary>Mọi đoạn thẳng (Line và từng cạnh PolyLine) của 1 CAD như hiển thị trong view, kèm layer và nét đứt hay
/// không (theo line pattern của layer), Z = 0 — kể cả trong block lồng nhau. Cung tròn bỏ qua.</summary>
internal static class CadLines
{
    private const int MaxRecursionDepth = 8;

    public static List<(string Layer, XYZ A, XYZ B, bool Dashed)> Read(Document doc, ImportInstance cad, View view, IReadOnlyCollection<string>? layers = null)
    {
        var result = new List<(string, XYZ, XYZ, bool)>();
        GeometryElement? geometry;
        try
        {
            geometry = cad.get_Geometry(new Options { View = view, IncludeNonVisibleObjects = false });
        }
        catch (Exception)
        {
            return result;
        }
        var dashed = new Dictionary<ElementId, bool>();
        if (geometry is not null) Walk(doc, geometry, layers, result, dashed, 0);
        return result;
    }

    private static void Walk(
        Document doc, GeometryElement geometry, IReadOnlyCollection<string>? layers, List<(string, XYZ, XYZ, bool)> result,
        Dictionary<ElementId, bool> dashed, int depth)
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
        Document doc, ElementId styleId, XYZ a, XYZ b, IReadOnlyCollection<string>? layers, List<(string, XYZ, XYZ, bool)> result,
        Dictionary<ElementId, bool> dashed)
    {
        var layer = CadGeometry.ResolveLayerName(doc, styleId);
        if (layers is { Count: > 0 } && !layers.Contains(layer)) return;
        var (flatA, flatB) = (new XYZ(a.X, a.Y, 0), new XYZ(b.X, b.Y, 0));
        if (flatA.DistanceTo(flatB) <= 1e-9) return;
        if (!dashed.TryGetValue(styleId, out var isDashed)) dashed[styleId] = isDashed = IsDashed(doc, styleId);
        result.Add((layer, flatA, flatB, isDashed));
    }

    /// <summary>Layer vẽ bằng nét đứt (nét khuất, nét tâm...): line pattern khác nét liền.</summary>
    private static bool IsDashed(Document doc, ElementId styleId)
    {
        if (doc.GetElement(styleId) is not GraphicsStyle { GraphicsStyleCategory: { } category }) return false;
        var pattern = category.GetLinePatternId(GraphicsStyleType.Projection);
        return pattern != ElementId.InvalidElementId && pattern != LinePatternElement.GetSolidPatternId();
    }
}
