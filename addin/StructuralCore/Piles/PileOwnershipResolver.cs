using Autodesk.Revit.DB;

namespace Bimtech.StructuralCore.Piles;

/// <summary>Xác định cọc thuộc đài nào bằng point-in-polygon trên đường bao CAD thật. Cọc nằm ngoài mọi đài chỉ được gán
/// cho đài gần nhất khi tim cọc cách biên đài không quá OwnerToleranceMm (cọc vẽ lệch nhẹ ra ngoài biên); xa hơn thì
/// không thuộc đài nào — một vòng tròn lạc (lỗ kỹ thuật, ký hiệu) không được lấy cao độ của một đài ở xa.</summary>
internal static class PileOwnershipResolver
{
    public const double OwnerToleranceMm = 300.0;

    public static void AssignOwners(IEnumerable<RawCircle> circles, IReadOnlyList<BoundaryRow> boundaries)
    {
        var tolerance = UnitUtils.ConvertToInternalUnits(OwnerToleranceMm, UnitTypeId.Millimeters);
        foreach (var circle in circles)
            circle.OwningBoundary = FindOwner(circle.Center, boundaries, tolerance);
    }

    private static BoundaryRow? FindOwner(XYZ point, IReadOnlyList<BoundaryRow> boundaries, double toleranceFeet)
    {
        foreach (var row in boundaries)
        {
            if (IsPointInPolygon(point, row.Boundary.Points))
                return row;
        }

        return boundaries
            .Select(row => (Row: row, Distance: DistanceToOutline(point, row.Boundary.Points)))
            .Where(entry => entry.Distance <= toleranceFeet)
            .OrderBy(entry => entry.Distance)
            .Select(entry => entry.Row)
            .FirstOrDefault();
    }

    /// <summary>Ray-casting chẵn-lẻ tiêu chuẩn trên đa giác khép kín (mặt phẳng XY).</summary>
    private static bool IsPointInPolygon(XYZ point, IReadOnlyList<XYZ> polygon)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var pi = polygon[i];
            var pj = polygon[j];
            if (pi.Y > point.Y != pj.Y > point.Y &&
                point.X < (pj.X - pi.X) * (point.Y - pi.Y) / (pj.Y - pi.Y) + pi.X)
                inside = !inside;
        }

        return inside;
    }

    /// <summary>Khoảng cách ngắn nhất từ điểm tới các cạnh đa giác (mặt phẳng XY).</summary>
    private static double DistanceToOutline(XYZ point, IReadOnlyList<XYZ> polygon)
    {
        var best = double.MaxValue;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = new XYZ(polygon[j].X, polygon[j].Y, 0);
            var ab = new XYZ(polygon[i].X - a.X, polygon[i].Y - a.Y, 0);
            var ap = new XYZ(point.X - a.X, point.Y - a.Y, 0);
            var lengthSquared = ab.DotProduct(ab);
            var t = lengthSquared < 1e-12 ? 0 : Math.Clamp(ap.DotProduct(ab) / lengthSquared, 0, 1);
            best = Math.Min(best, (ap - ab * t).GetLength());
        }

        return best;
    }
}
