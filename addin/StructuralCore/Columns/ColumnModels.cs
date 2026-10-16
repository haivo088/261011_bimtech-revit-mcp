using Autodesk.Revit.DB;
using Bimtech.StructuralCore.Cad;

namespace Bimtech.StructuralCore.Columns;

/// <summary>1 đường bao cột chữ nhật thô đọc được từ layer CAD, đã quy về hình chữ nhật xoay nhỏ nhất (min-area rectangle).</summary>
internal sealed class RawColumn
{
    public required XYZ Center { get; init; }

    /// <summary>Kích thước theo trục chính (feet) — trục chính có góc trong [0°, 90°).</summary>
    public required double LengthFeet { get; init; }

    /// <summary>Kích thước theo trục vuông góc với trục chính (feet).</summary>
    public required double WidthFeet { get; init; }

    /// <summary>Góc trục chính so với trục X thế giới (radian), luôn trong [0, π/2).</summary>
    public required double AngleRadians { get; init; }

    /// <summary>Toàn bộ đoạn thẳng gốc (Z=0) — mỗi đoạn là 1 cạnh CAD thật (từ PolyLine hoặc từ cụm Line rời rạc
    /// đã gộp), dùng để vẽ lại highlight đỏ đúng hình dạng đã vẽ, không phụ thuộc thứ tự nối cạnh.</summary>
    public required List<(XYZ A, XYZ B)> Segments { get; init; }
}

/// <summary>Kết quả quét hình học CAD của 1 ImportInstance, nhóm theo tên layer.</summary>
internal sealed class ColumnScanResult
{
    public required IReadOnlyDictionary<string, List<RawColumn>> ColumnsByLayer { get; init; }

    /// <summary>Tên layer kèm số cột, nhiều cột trước.</summary>
    public List<(string Name, int ColumnCount)> GetLayerSummaries() =>
        ColumnsByLayer
            .Select(kv => (kv.Key, kv.Value.Count))
            .OrderByDescending(e => e.Count)
            .ThenBy(e => e.Key, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>Cột thuộc các layer cho trước, tuỳ chọn chỉ lấy cột có tâm nằm trong vùng.</summary>
    public List<RawColumn> Select(IEnumerable<string> layerNames, CadGeometry.PlanRegion? region = null)
    {
        var columns = layerNames
            .Distinct(StringComparer.Ordinal)
            .SelectMany(name => ColumnsByLayer.TryGetValue(name, out var list) ? list : Enumerable.Empty<RawColumn>());
        if (region is { } r)
            columns = columns.Where(c => r.Contains(c.Center));
        return columns.ToList();
    }
}

/// <summary>Nhóm cột cùng kích thước b×h đã làm tròn bội 5mm (không phân biệt hướng xoay). Không chứa trạng thái UI.</summary>
internal sealed class ColumnSizeGroup
{
    /// <summary>Kích thước nhỏ hơn đã làm tròn bội 5mm.</summary>
    public required double SizeAMm { get; init; }

    /// <summary>Kích thước lớn hơn đã làm tròn bội 5mm.</summary>
    public required double SizeBMm { get; init; }

    public required List<RawColumn> Columns { get; init; }

    /// <summary>Khoá ổn định của nhóm, vd "500x300" — dùng làm tên nhóm khi trao đổi qua MCP.</summary>
    public string Key => $"{SizeBMm:0}x{SizeAMm:0}";

    public string SizeLabel => $"{SizeBMm:0}x{SizeAMm:0}mm";
}

/// <summary>1 nhóm cột kèm Type đã chọn — đầu vào của bước Đặt.</summary>
internal sealed record ColumnAssignment(ColumnSizeGroup Group, FamilySymbol? Symbol);

/// <summary>Kết quả tạo Type còn thiếu. Resolved: Type đã gán được cho từng nhóm (tạo mới hoặc dùng lại).</summary>
internal sealed record CreateTypesSummary(
    int Created, int Failed, List<string> Errors,
    List<FamilySymbol> CreatedSymbols,
    IReadOnlyDictionary<ColumnSizeGroup, FamilySymbol> Resolved);
