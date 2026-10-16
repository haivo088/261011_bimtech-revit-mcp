using Autodesk.Revit.DB;

namespace Bimtech.StructuralCore;

/// <summary>Kết quả đặt 1 phần tử (cột, dầm, cọc, đài...).</summary>
internal enum PlaceOutcome
{
    Created,
    Failed
}

internal sealed record PlaceResult(PlaceOutcome Outcome, string? ErrorMessage = null, ElementId? CreatedId = null);

/// <summary>Tổng kết 1 lượt Đặt cho các loại chỉ có 1 kiểu phần tử (cột, dầm).</summary>
internal sealed record PlaceSummary(
    int Created, int Failed, int Skipped,
    List<string> Errors,
    IReadOnlyList<ElementId> CreatedIds);

/// <summary>Tham số kích thước dạng Length của Type, quy đổi ra mm — null nếu Type không có tham số này.</summary>
internal static class SymbolParameters
{
    public static double? ReadLengthMm(FamilySymbol symbol, string paramName)
    {
        var p = symbol.LookupParameter(paramName);
        if (p is null || p.StorageType != StorageType.Double)
            return null;

        return UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.Millimeters);
    }

    /// <summary>Tên mới chưa trùng trong Family: "baseName", nếu trùng thì "baseName (2)", "baseName (3)"...</summary>
    public static string UniqueName(string baseName, ISet<string> existingNames)
    {
        var name = baseName;
        for (var suffix = 2; existingNames.Contains(name); suffix++)
            name = $"{baseName} ({suffix})";
        return name;
    }
}
