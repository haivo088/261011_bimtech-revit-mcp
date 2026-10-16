using Autodesk.Revit.DB;
using Bimtech.StructuralCore.Cad;

namespace Bimtech.StructuralCore.Beams;

/// <summary>1 dầm đọc được từ layer CAD — ghép từ 2 Line song song gần nhau, lấy tim làm đường đặt dầm.</summary>
internal sealed class RawBeam
{
    public required XYZ Start { get; init; }

    public required XYZ End { get; init; }

    /// <summary>Khoảng cách vuông góc giữa 2 Line đã ghép cặp (feet) — dùng làm bề rộng b của dầm.</summary>
    public required double WidthFeet { get; init; }

    /// <summary>2 Line CAD gốc (Z=0) đã ghép cặp — dùng để vẽ lại highlight đỏ đúng hình dạng đã vẽ.</summary>
    public required List<(XYZ A, XYZ B)> Segments { get; init; }

    /// <summary>Tên đọc được từ Text Note gần tim dầm nhất, dạng "TÊN(bxh)" (vd "D2-X7(220x600)") — null nếu
    /// không tìm được Text Note nào đủ gần/đúng định dạng. Gán SAU khi CadBeamScanner quét xong hình học
    /// (xem BeamTagReader.AttachTags), không phải lúc tạo RawBeam — không đổi thuật toán ghép cặp Line.</summary>
    public string? TagName { get; set; }

    /// <summary>Bề rộng b đọc từ Text (mm), độc lập với TagName (có thể có dù TagName null — Text dạng "bxh" trần không tên) — chỉ dùng xác nhận cặp Line lúc ghép, KHÔNG dùng làm b cuối cùng (xem WidthFeet/BeamPlacer.GroupBeams).</summary>
    public double? TagBMm { get; set; }

    /// <summary>Chiều cao h đọc từ Text (mm), độc lập với TagName — không đo được từ mặt bằng CAD nên đây là nguồn duy nhất.</summary>
    public double? TagHMm { get; set; }

    /// <summary>True nếu dầm này được BeamOrphanRepairer tự sửa từ 1 Line mồ côi (không có cặp CAD hợp lệ cùng layer) — 1 trong 2 Segments là Line vẽ thêm (synthetic), không phải Line CAD thật.</summary>
    public bool IsCrossLayerPair { get; init; }

    /// <summary>Ghi chú cách sửa mồ côi (layer gốc, có khớp Text hay không) — chỉ có giá trị khi IsCrossLayerPair = true, phục vụ hiển thị cảnh báo.</summary>
    public string? CrossLayerNote { get; init; }

    public XYZ Midpoint => new((Start.X + End.X) / 2, (Start.Y + End.Y) / 2, 0);
}

/// <summary>Kết quả quét hình học CAD của 1 ImportInstance, nhóm theo tên layer.</summary>
internal sealed class CadBeamScanResult
{
    public required IReadOnlyDictionary<string, List<RawBeam>> BeamsByLayer { get; init; }

    /// <summary>Toàn bộ đoạn thẳng thô theo layer, TRƯỚC khi ghép cặp — dùng để highlight xác nhận layer ở Bước 3 (kể cả đoạn chưa/không ghép được cặp), không dùng cho việc đặt dầm.</summary>
    public required IReadOnlyDictionary<string, List<(XYZ A, XYZ B)>> RawSegmentsByLayer { get; init; }

    /// <summary>Line không ghép được cặp cùng layer ở Pass 1 (mồ côi thật + bị "cướp" candidate) kèm tên layer gốc — input cho BeamOrphanRepairer.</summary>
    public required IReadOnlyList<(XYZ A, XYZ B, string LayerName)> Orphans { get; init; }

    /// <summary>Tên layer kèm số dầm đã ghép cặp, nhiều dầm trước.</summary>
    public List<(string Name, int Count)> GetLayerSummaries() =>
        BeamsByLayer
            .Select(kv => (kv.Key, kv.Value.Count))
            .OrderByDescending(e => e.Count)
            .ThenBy(e => e.Key, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>Dầm thuộc các layer cho trước, tuỳ chọn chỉ lấy dầm có trung điểm nằm trong vùng.</summary>
    public List<RawBeam> Select(IEnumerable<string> layerNames, CadGeometry.PlanRegion? region = null)
    {
        var beams = layerNames
            .Distinct(StringComparer.Ordinal)
            .SelectMany(name => BeamsByLayer.TryGetValue(name, out var list) ? list : Enumerable.Empty<RawBeam>());
        if (region is { } r)
            beams = beams.Where(b => r.Contains(b.Midpoint));
        return beams.ToList();
    }
}

/// <summary>Nhóm dầm, không chứa trạng thái UI — gộp theo tên đọc từ Text, theo cặp b×h (Text "bxh" không tên),
/// hoặc theo b hình học (không có Text nào).</summary>
internal sealed class BeamSizeGroup
{
    /// <summary>Tên đọc được từ Text — null nếu không có Text nào có tên gần dầm (có thể vẫn có SizeHMm nếu Text chỉ ghi "bxh" trần).</summary>
    public string? TagName { get; init; }

    /// <summary>Bề rộng b — LUÔN lấy từ hình học CAD đo được (đã làm tròn bội 5mm), kể cả khi TagName != null.</summary>
    public required double SizeBMm { get; init; }

    /// <summary>Chiều cao h đọc từ Text — null nếu hoàn toàn không có Text (khi đó không tạo được Type, chỉ khớp theo b).</summary>
    public double? SizeHMm { get; init; }

    public required List<RawBeam> Beams { get; init; }

    /// <summary>Khoá ổn định của nhóm khi trao đổi qua MCP: tên dầm, "bxh", hoặc "b220" khi chỉ biết bề rộng.</summary>
    public string Key => TagName ?? (SizeHMm is double h ? $"{SizeBMm:0}x{h:0}" : $"b{SizeBMm:0}");

    public string SizeLabel => SizeHMm is double h
        ? (TagName is not null ? $"{TagName} — {SizeBMm:0}x{h:0}mm" : $"{SizeBMm:0}x{h:0}mm")
        : $"{SizeBMm:0}mm";

    /// <summary>Mặc định chỉ dựng nhóm có tên đọc từ Text — nhóm không tên thường là nét phụ, người dùng tự chọn thêm.</summary>
    public bool ModelByDefault => !string.IsNullOrWhiteSpace(TagName);

    public bool HasCrossLayerBeams => Beams.Any(b => b.IsCrossLayerPair);
}

/// <summary>1 nhóm dầm sẽ dựng kèm Type đã chọn — đầu vào của bước Đặt (chỉ truyền nhóm cần dựng).</summary>
internal sealed record BeamAssignment(BeamSizeGroup Group, FamilySymbol? Symbol);

/// <summary>Kết quả tạo Type dầm còn thiếu. Resolved: Type đã gán được cho từng nhóm (tạo mới hoặc dùng lại).</summary>
internal sealed record BeamCreateTypesSummary(
    int Created, int Failed, List<string> Errors,
    List<FamilySymbol> CreatedSymbols,
    IReadOnlyDictionary<BeamSizeGroup, FamilySymbol> Resolved);
