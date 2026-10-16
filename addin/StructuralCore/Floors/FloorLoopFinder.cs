using Autodesk.Revit.DB;

namespace Bimtech.StructuralCore.Floors;

/// <summary>Các ô sàn khép kín tìm được, sau khi bỏ ô nhỏ hơn 1m² (coi là lỗ mở).</summary>
internal sealed record FloorLoopScan(
    List<List<Curve>> ValidLoops, int TotalLoops, int SkippedAsOpening, int CenterlineCount);

/// <summary>Tìm ô sàn từ tim dầm/vách thẳng — CHỈ ĐỌC model, không vẽ line nào. Tool WPF đi qua bước trung gian là
/// DetailLine (để người dùng sửa tay); MCP gọi thẳng FromModel: thu tim → dò mặt kín → lọc diện tích, cùng thuật toán.</summary>
internal static class FloorLoopFinder
{
    public static readonly double MinFloorAreaSqFeet = UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.SquareMeters);

    /// <summary>Thu tim dầm (Reference Level = level) và tim vách thẳng cắt qua level trong view, rồi tìm ô kín.</summary>
    public static FloorLoopScan FromModel(Document doc, ViewPlan view, Level level)
    {
        var segments = BeamLoopScanner.CollectCenterlineSegments(doc, view, level);
        var scan = FromCurves(segments.Cast<Curve>().ToList());
        return scan with { CenterlineCount = segments.Count };
    }

    /// <summary>Dò mặt kín từ các đường thẳng phẳng cho trước, chia ô thành ô sàn hợp lệ và ô nhỏ hơn 1m².</summary>
    public static FloorLoopScan FromCurves(IReadOnlyList<Curve> curves)
    {
        var loops = BeamLoopScanner.TraceClosedLoops(curves);
        var valid = new List<List<Curve>>();
        var skippedAsOpening = 0;
        foreach (var loop in loops)
        {
            if (PolygonMath.AreaAbs(PolygonMath.Tessellate(loop)) < MinFloorAreaSqFeet)
                skippedAsOpening++;
            else
                valid.Add(loop);
        }
        return new FloorLoopScan(valid, loops.Count, skippedAsOpening, curves.Count);
    }

    public static double AreaM2(List<Curve> loop) =>
        UnitUtils.ConvertFromInternalUnits(PolygonMath.AreaAbs(PolygonMath.Tessellate(loop)), UnitTypeId.SquareMeters);
}

/// <summary>Floor Type mặc định của công ty — "BTC_SanKC_150", lớp kết cấu 150mm.</summary>
internal static class FloorDefaults
{
    public const string DefaultFloorTypeName = "BTC_SanKC_150";
    public const double DefaultFloorThicknessMm = 150.0;

    public static List<FloorType> CollectFloorTypes(Document doc) =>
        new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_Floors)
            .OfClass(typeof(FloorType))
            .Cast<FloorType>()
            .ToList();

    /// <summary>Tạo Floor Type BTC_SanKC_150 từ Type có sẵn nếu project chưa có. Mở Transaction riêng; lỗi thì bỏ qua
    /// (rollback) như tool WPF.</summary>
    public static void EnsureDefaultFloorType(Document doc)
    {
        var types = CollectFloorTypes(doc);

        if (types.Any(t => string.Equals(t.Name, DefaultFloorTypeName, StringComparison.OrdinalIgnoreCase)))
            return;

        var seed = types.FirstOrDefault(t => t.GetCompoundStructure() is not null);
        if (seed is null)
            return;

        using var tx = new Transaction(doc, "Tạo Floor Type mặc định");
        tx.Start();

        try
        {
            var newType = (FloorType)seed.Duplicate(DefaultFloorTypeName);
            var structure = newType.GetCompoundStructure();
            if (structure is null)
                throw new InvalidOperationException("Floor Type gốc không có cấu tạo lớp.");

            var layers = structure.GetLayers();
            var structuralIndex = -1;
            for (var index = 0; index < layers.Count; index++)
            {
                if (layers[index].Function != MaterialFunctionAssignment.Structure)
                    continue;

                structuralIndex = index;
                break;
            }
            if (structuralIndex < 0 && layers.Count == 1)
                structuralIndex = 0;
            if (structuralIndex < 0)
                throw new InvalidOperationException("Floor Type gốc không có lớp Structure.");

            var otherWidth = layers.Where((_, index) => index != structuralIndex).Sum(layer => layer.Width);
            var targetWidth = UnitUtils.ConvertToInternalUnits(DefaultFloorThicknessMm, UnitTypeId.Millimeters);
            if (targetWidth <= otherWidth)
                throw new InvalidOperationException("Các lớp khác của Floor Type gốc đã dày quá 150mm.");

            structure.SetLayerWidth(structuralIndex, targetWidth - otherWidth);
            newType.SetCompoundStructure(structure);
            tx.Commit();
        }
        catch
        {
            tx.RollBack();
        }
    }
}
