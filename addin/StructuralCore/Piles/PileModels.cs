using Autodesk.Revit.DB;

namespace Bimtech.StructuralCore.Piles;

/// <summary>1 cọc nhận diện từ CAD (PileRecognizer): tâm và bán kính.</summary>
internal sealed class RawCircle
{
    public required XYZ Center { get; init; }
    public required double RadiusFeet { get; init; }

    /// <summary>Đài chứa cọc này (point-in-polygon, gán sau khi quét) — null nếu chưa xác định được.</summary>
    public BoundaryRow? OwningBoundary { get; set; }
}

/// <summary>1 đường bao đài nhận diện từ CAD (PileRecognizer).</summary>
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

    /// <summary>Ký hiệu đài đọc từ Text Note hay chữ DWG trong/cạnh đài, vd "Đ1"; chiều dày ghi kèm, vd "(2000x2000x1200)" hay "h=1200".</summary>
    public string? TagName { get; set; }
    public string? TagText { get; set; }
    public double? TagThicknessMm { get; set; }

    public string Label => TagName is null ? $"Đài #{Index}" : $"Đài {TagName} #{Index}";
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
