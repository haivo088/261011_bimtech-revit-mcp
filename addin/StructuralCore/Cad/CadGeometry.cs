using Autodesk.Revit.DB;

namespace Bimtech.StructuralCore.Cad;

/// <summary>Hàm hình học 2D dùng chung cho mọi bộ quét CAD (cột, cọc, dầm...) — chỉ làm việc trên mặt phẳng XY.</summary>
internal static class CadGeometry
{
    public const string UnknownLayerName = "(Không xác định layer)";

    /// <summary>Hình chữ nhật xoay nhỏ nhất bao quanh đa giác (rotating calipers trên convex hull) — null nếu đa giác suy biến.
    /// Trả về tâm, kích thước theo trục chính, kích thước theo trục vuông góc, góc trục chính trong [0, π/2).</summary>
    public static (XYZ Center, double LengthFeet, double WidthFeet, double AngleRadians)? MinAreaRectangle(List<XYZ> points)
    {
        var hull = ConvexHull(points);
        if (hull.Count < 3)
            return null;

        var bestArea = double.MaxValue;
        XYZ bestD = XYZ.BasisX;
        XYZ bestP = XYZ.BasisY;
        double bestMinD = 0, bestMaxD = 0, bestMinP = 0, bestMaxP = 0;

        for (var i = 0; i < hull.Count; i++)
        {
            var p0 = hull[i];
            var p1 = hull[(i + 1) % hull.Count];
            var edge = p1 - p0;
            var len = edge.GetLength();
            if (len < 1e-9)
                continue;

            var d = edge / len;
            var p = new XYZ(-d.Y, d.X, 0);

            double minD = double.MaxValue, maxD = double.MinValue, minP = double.MaxValue, maxP = double.MinValue;
            foreach (var pt in hull)
            {
                var projD = pt.X * d.X + pt.Y * d.Y;
                var projP = pt.X * p.X + pt.Y * p.Y;
                if (projD < minD) minD = projD;
                if (projD > maxD) maxD = projD;
                if (projP < minP) minP = projP;
                if (projP > maxP) maxP = projP;
            }

            var area = (maxD - minD) * (maxP - minP);
            if (area < bestArea)
            {
                bestArea = area;
                bestD = d;
                bestP = p;
                bestMinD = minD;
                bestMaxD = maxD;
                bestMinP = minP;
                bestMaxP = maxP;
            }
        }

        if (bestArea >= double.MaxValue)
            return null;

        var centerXY = bestD * ((bestMinD + bestMaxD) / 2) + bestP * ((bestMinP + bestMaxP) / 2);
        var center = new XYZ(centerXY.X, centerXY.Y, 0);
        var lengthAlongD = bestMaxD - bestMinD;
        var widthAlongP = bestMaxP - bestMinP;

        var angle = Math.Atan2(bestD.Y, bestD.X);
        angle = ((angle % Math.PI) + Math.PI) % Math.PI;

        double canonicalAngle, canonicalLength, canonicalWidth;
        if (angle < Math.PI / 2)
        {
            canonicalAngle = angle;
            canonicalLength = lengthAlongD;
            canonicalWidth = widthAlongP;
        }
        else
        {
            canonicalAngle = angle - Math.PI / 2;
            canonicalLength = widthAlongP;
            canonicalWidth = lengthAlongD;
        }

        return (center, canonicalLength, canonicalWidth, canonicalAngle);
    }

    /// <summary>Convex hull qua thuật toán monotone chain — trả về các đỉnh theo chiều ngược kim đồng hồ, không lặp điểm đầu/cuối.
    /// Gộp điểm trùng toạ độ qua GroupBy (đủ để loại điểm đóng lặp lại của PolyLine khép kín, không cần biết trước polyline hở hay kín).</summary>
    public static List<XYZ> ConvexHull(List<XYZ> points)
    {
        var pts = points
            .GroupBy(p => (Math.Round(p.X, 6), Math.Round(p.Y, 6)))
            .Select(g => g.First())
            .OrderBy(p => p.X).ThenBy(p => p.Y)
            .ToList();

        if (pts.Count < 3)
            return pts;

        var lower = new List<XYZ>();
        foreach (var p in pts)
        {
            while (lower.Count >= 2 && Cross(lower[^2], lower[^1], p) <= 0)
                lower.RemoveAt(lower.Count - 1);
            lower.Add(p);
        }

        var upper = new List<XYZ>();
        for (var i = pts.Count - 1; i >= 0; i--)
        {
            var p = pts[i];
            while (upper.Count >= 2 && Cross(upper[^2], upper[^1], p) <= 0)
                upper.RemoveAt(upper.Count - 1);
            upper.Add(p);
        }

        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        return lower;
    }

    private static double Cross(XYZ o, XYZ a, XYZ b) =>
        (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

    /// <summary>Tên layer CAD của 1 đối tượng hình học (theo GraphicsStyle) — UnknownLayerName nếu không đọc được.</summary>
    public static string ResolveLayerName(Document doc, ElementId styleId)
    {
        if (styleId == ElementId.InvalidElementId)
            return UnknownLayerName;

        if (doc.GetElement(styleId) is not GraphicsStyle style)
            return UnknownLayerName;

        var name = style.GraphicsStyleCategory?.Name;
        return string.IsNullOrWhiteSpace(name) ? UnknownLayerName : name;
    }

    /// <summary>Vùng chữ nhật trên mặt bằng (feet, toạ độ model) để giới hạn phạm vi xử lý.</summary>
    public readonly record struct PlanRegion(double MinX, double MaxX, double MinY, double MaxY)
    {
        public bool Contains(XYZ point) =>
            point.X >= MinX && point.X <= MaxX && point.Y >= MinY && point.Y <= MaxY;
    }
}
