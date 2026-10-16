using Autodesk.Revit.DB;
using Bimtech.StructuralCore.Cad;

namespace Bimtech.StructuralCore.Piles;

/// <summary>1 circle thô đọc được từ layer cọc.</summary>
internal sealed class RawCircle
{
    public required XYZ Center { get; init; }
    public required double RadiusFeet { get; init; }

    /// <summary>Đài chứa cọc này (point-in-polygon, gán sau khi quét) — null nếu chưa xác định được.</summary>
    public BoundaryRow? OwningBoundary { get; set; }
}

/// <summary>1 đường bao khép kín thô đọc được từ layer đài móng.</summary>
internal sealed class RawBoundary
{
    public required XYZ Center { get; init; }

    /// <summary>Kích thước bounding box trục X/Y (feet) — Length luôn ≥ Width.</summary>
    public required double LengthFeet { get; init; }
    public required double WidthFeet { get; init; }

    /// <summary>Toàn bộ điểm đa giác gốc (đã khép kín, Z=0) — dùng để vẽ lại highlight đỏ.</summary>
    public required List<XYZ> Points { get; init; }
}

/// <summary>1 đài móng đã quét (sau khi gộp đường bao vẽ trùng) — Type đài là 1 FloorType dùng chung cho cả lượt Đặt.</summary>
internal sealed class BoundaryRow
{
    public required int Index { get; init; }
    public required RawBoundary Boundary { get; init; }

    public string Label => $"Đài #{Index}";
}

/// <summary>Kết quả quét hình học CAD của 1 ImportInstance, nhóm theo tên layer.</summary>
internal sealed class CadPileScanResult
{
    public required IReadOnlyDictionary<string, List<RawCircle>> CirclesByLayer { get; init; }
    public required IReadOnlyDictionary<string, List<RawBoundary>> BoundariesByLayer { get; init; }

    /// <summary>Tên layer kèm số circle và số đường bao, nhiều hình nhất trước.</summary>
    public List<(string Name, int CircleCount, int BoundaryCount)> GetLayerSummaries()
    {
        var names = new HashSet<string>(CirclesByLayer.Keys);
        names.UnionWith(BoundariesByLayer.Keys);

        return names
            .Select(name => (
                name,
                CirclesByLayer.TryGetValue(name, out var c) ? c.Count : 0,
                BoundariesByLayer.TryGetValue(name, out var b) ? b.Count : 0))
            .OrderByDescending(e => e.Item2 + e.Item3)
            .ThenBy(e => e.name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public List<RawCircle> SelectCircles(IEnumerable<string> layerNames, CadGeometry.PlanRegion? region = null) =>
        Select(CirclesByLayer, layerNames, c => c.Center, region);

    public List<RawBoundary> SelectBoundaries(IEnumerable<string> layerNames, CadGeometry.PlanRegion? region = null) =>
        Select(BoundariesByLayer, layerNames, b => b.Center, region);

    private static List<T> Select<T>(
        IReadOnlyDictionary<string, List<T>> byLayer, IEnumerable<string> layerNames, Func<T, XYZ> centerOf, CadGeometry.PlanRegion? region)
    {
        var items = layerNames
            .Distinct(StringComparer.Ordinal)
            .SelectMany(name => byLayer.TryGetValue(name, out var list) ? list : Enumerable.Empty<T>());
        if (region is { } r)
            items = items.Where(item => r.Contains(centerOf(item)));
        return items.ToList();
    }
}

/// <summary>Nhóm cọc cùng đường kính đã làm tròn — không chứa trạng thái UI.</summary>
internal sealed class PileSizeGroup
{
    public required double DiameterMm { get; init; }
    public required List<RawCircle> Circles { get; init; }

    public string DiameterLabel => $"Ø{DiameterMm:0}mm";

    /// <summary>Khoá ổn định của nhóm khi trao đổi qua MCP, vd "D600".</summary>
    public string Key => $"D{DiameterMm:0}";
}

/// <summary>1 nhóm cọc kèm Type đã chọn — đầu vào của bước Đặt.</summary>
internal sealed record PileAssignment(PileSizeGroup Group, FamilySymbol? Symbol);

/// <summary>Kết quả tạo Type cọc còn thiếu. Resolved: Type mới đã gán cho từng nhóm.</summary>
internal sealed record PileCreateTypesSummary(
    int Created, int Failed, List<string> Errors,
    List<FamilySymbol> CreatedSymbols,
    IReadOnlyDictionary<PileSizeGroup, FamilySymbol> Resolved);

internal sealed record PilePlaceSummary(
    int PilesCreated, int PilesFailed, int PilesSkipped,
    int CapsCreated, int CapsFailed, int CapsSkipped,
    List<string> Errors,
    IReadOnlyList<ElementId> CreatedPileIds,
    IReadOnlyList<ElementId> CreatedCapIds,
    /// <summary>1 ElementId? song song từng dòng đài trong bảng — mới tạo, đã có sẵn (trùng), hoặc null nếu lỗi/không đặt được.</summary>
    IReadOnlyList<ElementId?> ResolvedCapIds,
    /// <summary>CurveLoop sạch (đã regularize) THẬT SỰ dùng để dựng từng đài mới tạo trong lượt này — lớp lót đọc lại từ đây thay vì đọc qua Sketch.Profile để tránh sai số round-trip.</summary>
    IReadOnlyDictionary<ElementId, CurveLoop> CreatedCapProfiles);
