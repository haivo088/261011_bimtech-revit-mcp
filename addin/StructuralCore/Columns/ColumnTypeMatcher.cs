using Autodesk.Revit.DB;

namespace Bimtech.StructuralCore.Columns;

/// <summary>Khớp Type cột theo tham số tiết diện b/h của Type với kích thước nhóm cột (cặp không thứ tự).</summary>
internal static class ColumnTypeMatcher
{
    /// <summary>Tên Parameter kích thước tiết diện — mặc định theo quy ước phổ biến của Revit, CHƯA đối chiếu
    /// với Family cột chuẩn công ty (cần verify trên Revit thật, xem claude/PlaceColumn-handoff.md).</summary>
    public const string ColumnBParam = "b";
    public const string ColumnHParam = "h";

    public const double MatchToleranceMm = 2.0;

    /// <summary>Mọi Type trong category Structural Columns của project.</summary>
    public static List<FamilySymbol> CollectColumnTypes(Document doc) =>
        new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_StructuralColumns)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .ToList();

    public static bool IsMatch(FamilySymbol symbol, double sizeAMm, double sizeBMm)
    {
        var b = ReadLengthMm(symbol, ColumnBParam);
        var h = ReadLengthMm(symbol, ColumnHParam);
        if (b is null || h is null) return false;

        var matchesStraight = Math.Abs(b.Value - sizeAMm) <= MatchToleranceMm && Math.Abs(h.Value - sizeBMm) <= MatchToleranceMm;
        var matchesSwapped = Math.Abs(b.Value - sizeBMm) <= MatchToleranceMm && Math.Abs(h.Value - sizeAMm) <= MatchToleranceMm;
        return matchesStraight || matchesSwapped;
    }

    public static T? FindExactMatch<T>(double sizeAMm, double sizeBMm, IEnumerable<T> choices, Func<T, FamilySymbol> symbolOf)
        where T : class =>
        choices.FirstOrDefault(choice => IsMatch(symbolOf(choice), sizeAMm, sizeBMm));

    /// <summary>Type có (b, h) gần đúng nhất với cặp kích thước nhóm cột — thử cả 2 chiều gán vì cặp kích thước
    /// nhóm không có thứ tự cố định. Null nếu không Type nào có đủ cả 2 parameter.</summary>
    public static T? AutoMatch<T>(double sizeAMm, double sizeBMm, IEnumerable<T> choices, Func<T, FamilySymbol> symbolOf)
        where T : class
    {
        T? best = null;
        var bestDiff = double.MaxValue;

        foreach (var choice in choices)
        {
            var symbol = symbolOf(choice);
            var b = ReadLengthMm(symbol, ColumnBParam);
            var h = ReadLengthMm(symbol, ColumnHParam);
            if (b is null || h is null)
                continue;

            var diffStraight = Math.Abs(b.Value - sizeAMm) + Math.Abs(h.Value - sizeBMm);
            var diffSwapped = Math.Abs(b.Value - sizeBMm) + Math.Abs(h.Value - sizeAMm);
            var diff = Math.Min(diffStraight, diffSwapped);

            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = choice;
            }
        }

        return best;
    }

    /// <summary>Đọc 1 parameter Length của Type, quy đổi ra mm — null nếu Type không có parameter này.</summary>
    public static double? ReadLengthMm(FamilySymbol symbol, string paramName)
    {
        var p = symbol.LookupParameter(paramName);
        if (p is null || p.StorageType != StorageType.Double)
            return null;

        return UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.Millimeters);
    }
}
