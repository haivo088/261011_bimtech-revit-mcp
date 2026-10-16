using Autodesk.Revit.DB;
using Bimtech.StructuralCore.Cad;

namespace Bimtech.StructuralCore.Walls;

/// <summary>1 tường đọc được từ layer CAD — ghép từ 2 Line song song gần nhau, lấy tim làm đường đặt tường.</summary>
internal sealed class RawWall
{
    public required XYZ Start { get; init; }

    public required XYZ End { get; init; }

    /// <summary>Khoảng cách vuông góc giữa 2 Line đã ghép cặp (feet) — dùng làm bề rộng b của tường.</summary>
    public required double WidthFeet { get; init; }

    /// <summary>2 Line CAD gốc (Z=0) đã ghép cặp — dùng để vẽ lại highlight đỏ đúng hình dạng đã vẽ.</summary>
    public required List<(XYZ A, XYZ B)> Segments { get; init; }

    /// <summary>Tên đọc được từ Text Note gần tim tường nhất, dạng "TÊN(bxh)" (vd "D2-X7(220x600)") — null nếu
    /// không tìm được Text Note nào đủ gần/đúng định dạng. Gán SAU khi CadWallScanner quét xong hình học
    /// (xem WallTagReader.AttachTags), không phải lúc tạo RawWall — không đổi thuật toán ghép cặp Line.</summary>
    public string? TagName { get; set; }

    /// <summary>Bề rộng b đọc từ Text (mm), độc lập với TagName (có thể có dù TagName null — Text dạng "bxh" trần không tên) — chỉ dùng xác nhận cặp Line lúc ghép, KHÔNG dùng làm b cuối cùng (xem WidthFeet/WallPlacer.GroupWalls).</summary>
    public double? TagBMm { get; set; }

    /// <summary>Chiều cao h đọc từ Text (mm), độc lập với TagName — không đo được từ mặt bằng CAD nên đây là nguồn duy nhất.</summary>
    public double? TagHMm { get; set; }

    /// <summary>True nếu tường này được WallOrphanRepairer tự sửa từ 1 Line mồ côi (không có cặp CAD hợp lệ cùng layer) — 1 trong 2 Segments là Line vẽ thêm (synthetic), không phải Line CAD thật.</summary>
    public bool IsCrossLayerPair { get; init; }

    /// <summary>Ghi chú cách sửa mồ côi (layer gốc, có khớp Text hay không) — chỉ có giá trị khi IsCrossLayerPair = true, phục vụ hiển thị cảnh báo.</summary>
    public string? CrossLayerNote { get; init; }

    /// <summary>Vách nhận từ hatch (WallRecognizer): true = đủ bằng chứng (dựng mặc định), false = nên xem lại; null cho
    /// vách ghép từ cặp Line kiểu cũ.</summary>
    public bool? Sure { get; init; }

    /// <summary>Bằng chứng có dấu của vách nhận từ hatch, vd "+1 nằm trên trục" — hiện trong cửa sổ duyệt.</summary>
    public IReadOnlyList<string>? Evidence { get; init; }

    /// <summary>Trục mà tim hoặc mặt vách nằm trên (vách nhận từ hatch).</summary>
    public string? Grid { get; init; }

    public XYZ Midpoint => new((Start.X + End.X) / 2, (Start.Y + End.Y) / 2, 0);
}

/// <summary>Kết quả quét hình học CAD của 1 ImportInstance, nhóm theo tên layer.</summary>
internal sealed class CadWallScanResult
{
    public required IReadOnlyDictionary<string, List<RawWall>> WallsByLayer { get; init; }

    /// <summary>Toàn bộ đoạn thẳng thô theo layer, TRƯỚC khi ghép cặp — dùng để highlight xác nhận layer ở Bước 3 (kể cả đoạn chưa/không ghép được cặp), không dùng cho việc đặt tường.</summary>
    public required IReadOnlyDictionary<string, List<(XYZ A, XYZ B)>> RawSegmentsByLayer { get; init; }

    /// <summary>Line không ghép được cặp cùng layer ở Pass 1 (mồ côi thật + bị "cướp" candidate) kèm tên layer gốc — input cho WallOrphanRepairer.</summary>
    public required IReadOnlyList<(XYZ A, XYZ B, string LayerName)> Orphans { get; init; }

    /// <summary>Tên layer kèm số tường đã ghép cặp, nhiều tường trước.</summary>
    public List<(string Name, int Count)> GetLayerSummaries() =>
        WallsByLayer
            .Select(kv => (kv.Key, kv.Value.Count))
            .OrderByDescending(e => e.Count)
            .ThenBy(e => e.Key, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>Tường thuộc các layer cho trước, tuỳ chọn chỉ lấy tường có trung điểm nằm trong vùng.</summary>
    public List<RawWall> Select(IEnumerable<string> layerNames, CadGeometry.PlanRegion? region = null)
    {
        var walls = layerNames
            .Distinct(StringComparer.Ordinal)
            .SelectMany(name => WallsByLayer.TryGetValue(name, out var list) ? list : Enumerable.Empty<RawWall>());
        if (region is { } r)
            walls = walls.Where(w => r.Contains(w.Midpoint));
        return walls.ToList();
    }
}

/// <summary>Nhóm tường cùng tên đọc từ Text và cùng bề dày b (làm tròn 5mm) — không chứa trạng thái UI.</summary>
internal sealed class WallSizeGroup
{
    /// <summary>Tên đọc được từ Text — null nếu không có Text nào có tên gần tường.</summary>
    public string? TagName { get; init; }

    /// <summary>Bề dày b — luôn lấy từ hình học CAD đo được, làm tròn bội 5mm.</summary>
    public required double SizeBMm { get; init; }

    public required List<RawWall> Walls { get; init; }

    /// <summary>Nhóm vách nhận từ hatch: true = đủ bằng chứng, false = nên xem lại; null cho nhóm kiểu cũ (cặp Line).</summary>
    public bool? Sure { get; init; }

    /// <summary>Khoá ổn định của nhóm khi trao đổi qua MCP: "TÊN|200", hoặc "w200" cho tường không có tên; vách nhận từ
    /// hatch: "w200" (đủ bằng chứng) hoặc "w200-maybe" (nên xem lại).</summary>
    public string Key => Sure is { } sure ? (sure ? $"w{SizeBMm:0}" : $"w{SizeBMm:0}-maybe")
        : TagName is null ? $"w{SizeBMm:0}" : $"{TagName}|{SizeBMm:0}";

    public string SizeLabel => TagName is not null ? $"{TagName} — {SizeBMm:0}mm" : $"{SizeBMm:0}mm";

    /// <summary>Mặc định chỉ dựng nhóm có tên đọc từ Text — nhóm không tên thường là nét phụ, người dùng tự chọn thêm.</summary>
    public bool ModelByDefault => Sure ?? !string.IsNullOrWhiteSpace(TagName);

    public bool HasCrossLayerWalls => Walls.Any(w => w.IsCrossLayerPair);
}

/// <summary>1 nhóm tường sẽ dựng kèm Wall Type đã chọn — đầu vào của bước Đặt (chỉ truyền nhóm cần dựng).</summary>
internal sealed record WallAssignment(WallSizeGroup Group, WallType? Type);

/// <summary>Kết quả tạo Wall Type còn thiếu. Resolved: Type đã gán được cho từng nhóm (tạo mới hoặc dùng lại).</summary>
internal sealed record WallCreateTypesSummary(
    int Created, int Failed, List<string> Errors,
    List<WallType> CreatedTypes,
    IReadOnlyDictionary<WallSizeGroup, WallType> Resolved);

/// <summary>Xem trước bước Đặt cho 1 nhóm: số tường sẽ tạo ít nhất 1 đoạn, số đoạn sẽ tạo (sau khi trừ phần trùng
/// tường cũ), số tường bị bỏ qua vì đã có đủ. Problem khác null nghĩa là cả nhóm sẽ lỗi.</summary>
internal sealed record WallPlacementPreview(
    WallSizeGroup Group, int WallsToPlace, int PiecesToCreate, int FullyCovered, string? Problem);
