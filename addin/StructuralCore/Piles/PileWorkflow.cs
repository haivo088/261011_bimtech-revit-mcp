using Autodesk.Revit.DB;
using View = Autodesk.Revit.DB.View;

namespace Bimtech.StructuralCore.Piles;

/// <summary>Type Foundation Slab mặc định cho đài và lớp lót — tên/độ dày theo chuẩn công ty.</summary>
internal static class PileDefaults
{
    public const string CapTypeName = "BTC-DaiMong_1500";
    public const double CapThicknessMm = 1500.0;
    public const string LotTypeName = "BTC-BeTongLot_100";
    public const double LotThicknessMm = 100.0;
    public const double LotOffsetMm = 50.0;

    public static List<FloorType> CollectFoundationSlabTypes(Document doc) =>
        new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_StructuralFoundation)
            .OfClass(typeof(FloorType))
            .Cast<FloorType>()
            .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public static FloorType? FindByName(IEnumerable<FloorType> types, string name) =>
        types.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Tên các Type mặc định còn thiếu mà tạo được (project phải có sẵn ít nhất 1 Type Foundation Slab làm seed).</summary>
    public static List<string> MissingDefaultTypeNames(IReadOnlyList<FloorType> existingTypes)
    {
        if (existingTypes.Count == 0) return [];
        return new[] { CapTypeName, LotTypeName }.Where(name => FindByName(existingTypes, name) is null).ToList();
    }

    /// <summary>Tự tạo 2 Type Foundation Slab mặc định (đài + lớp lót) nếu project chưa có sẵn theo đúng tên — duplicate từ Type Foundation Slab đầu tiên có sẵn làm seed. Project hoàn toàn chưa có Type Foundation Slab nào (không có seed để duplicate) thì bỏ qua, giữ nguyên danh sách rỗng.</summary>
    public static List<FloorType> EnsureFoundationSlabTypes(Document doc, List<FloorType> existingTypes)
    {
        var missingCap = existingTypes.All(t => !string.Equals(t.Name, CapTypeName, StringComparison.OrdinalIgnoreCase));
        var missingLot = existingTypes.All(t => !string.Equals(t.Name, LotTypeName, StringComparison.OrdinalIgnoreCase));
        if ((!missingCap && !missingLot) || existingTypes.Count == 0)
            return existingTypes;

        using var tx = new Transaction(doc, "Tạo Type đài/lớp lót mặc định");
        tx.Start();

        try
        {
            var seed = existingTypes[0];
            if (missingCap)
                DuplicateFoundationSlabType(seed, CapTypeName, CapThicknessMm);
            if (missingLot)
                DuplicateFoundationSlabType(seed, LotTypeName, LotThicknessMm);

            tx.Commit();
        }
        catch (Exception)
        {
            tx.RollBack();
            return existingTypes;
        }

        return CollectFoundationSlabTypes(doc);
    }

    /// <summary>Type Foundation Slab có tổng chiều dày đúng thicknessMm (sai số 1mm), ưu tiên Type tên "BTC-DaiMong...".</summary>
    public static FloorType? FindByThickness(IEnumerable<FloorType> types, double thicknessMm) =>
        types.Where(t => PileFoundationPlacer.CapThicknessFeet(t) is { } feet
                && Math.Abs(UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters) - thicknessMm) <= 1)
            .OrderByDescending(t => t.Name.StartsWith("BTC-DaiMong", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();

    /// <summary>Type đài đúng chiều dày, trong Transaction do caller mở sẵn: dùng lại Type có sẵn (kể cả Type vừa tạo ở mục
    /// trước), không thì duplicate <paramref name="template"/> thành "BTC-DaiMong_h" và chỉnh lớp Structure dày nhất cho đủ
    /// tổng chiều dày. Ném InvalidOperationException khi không chỉnh được.</summary>
    public static (FloorType Type, bool Created) ResolveOrCreateCapType(Document doc, double thicknessMm, FloorType template)
    {
        var types = CollectFoundationSlabTypes(doc);
        if (FindByThickness(types, thicknessMm) is { } existing)
            return (existing, false);

        var name = SymbolParameters.UniqueName($"BTC-DaiMong_{Math.Round(thicknessMm):0}", types.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
        var created = (FloorType)template.Duplicate(name);
        var structure = created.GetCompoundStructure()
            ?? throw new InvalidOperationException($"Type đài mẫu \"{template.Name}\" không có cấu tạo lớp.");
        var layers = structure.GetLayers();
        if (layers.Count == 0) throw new InvalidOperationException($"Type đài mẫu \"{template.Name}\" không có lớp nào.");
        var index = Enumerable.Range(0, layers.Count)
            .Where(i => layers[i].Function == MaterialFunctionAssignment.Structure)
            .OrderByDescending(i => layers[i].Width).Cast<int?>().FirstOrDefault()
            ?? Enumerable.Range(0, layers.Count).OrderByDescending(i => layers[i].Width).First();
        var others = layers.Where((_, i) => i != index).Sum(layer => layer.Width);
        var width = UnitUtils.ConvertToInternalUnits(thicknessMm, UnitTypeId.Millimeters) - others;
        if (width <= UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Millimeters))
            throw new InvalidOperationException($"Các lớp còn lại của Type đài mẫu \"{template.Name}\" đã dày hơn {thicknessMm:0} mm.");
        structure.SetLayerWidth(index, width);
        created.SetCompoundStructure(structure);
        return (created, true);
    }

    /// <summary>Duplicate 1 Type Foundation Slab, đặt tên mới + chỉnh độ dày (chỉ áp dụng khi Type gốc có đúng 1 lớp — cấu tạo nhiều lớp thì giữ nguyên, không đoán lớp nào là lớp chính).</summary>
    private static void DuplicateFoundationSlabType(FloorType seed, string name, double thicknessMm)
    {
        var duplicated = (FloorType)seed.Duplicate(name);

        var structure = duplicated.GetCompoundStructure();
        if (structure is null)
            return;

        var layers = structure.GetLayers();
        if (layers.Count != 1)
            return;

        var thicknessFeet = UnitUtils.ConvertToInternalUnits(thicknessMm, UnitTypeId.Millimeters);
        structure.SetLayerWidth(0, thicknessFeet);
        duplicated.SetCompoundStructure(structure);
    }
}

/// <summary>Kết quả cả lượt Đặt cọc: cọc + đài, lớp lót (nếu có Type lót), bắt tròn kích thước theo lưới, join cọc với đài.</summary>
internal sealed record PileWorkflowResult(
    PilePlaceSummary Place,
    LotFloorSummary? Lot,
    SnapSummary? Snap,
    JoinSummary? Join);

/// <summary>Chuỗi thao tác 1 lượt Đặt cọc, giống hệt nút "Đặt" của tool WPF — dùng chung để PlacePileFoundation và MCP
/// luôn ra cùng kết quả. Mỗi bước tự mở Transaction riêng như trước khi tách khỏi cửa sổ.</summary>
internal static class PileWorkflow
{
    /// <summary>Góc lưới trục chính trong view, dùng để nắn biên dạng đài cho thẳng theo lưới — null nếu không có lưới.</summary>
    public static double? DetectGridTheta(Document doc, View? view) =>
        view is null ? null : PileGridSnapper.DetectThetaDeg(doc, view);

    /// <param name="view">View dùng để dò góc lưới, bắt tròn kích thước và join (tool WPF truyền view đang mở).</param>
    /// <param name="lotType">Type lớp lót; null thì không đặt lớp lót.</param>
    /// <param name="pileTypes">Mọi Type cọc trong project — dùng tìm Family cọc khi join với đài.</param>
    public static PileWorkflowResult PlaceAll(
        Document doc, View? view,
        IReadOnlyList<PileAssignment> pileAssignments, IReadOnlyList<BoundaryRow> boundaryRows,
        Level? capLevel, FloorType? capFloorType, double embedmentMm,
        FloorType? lotType, double lotOffsetMm, IEnumerable<FamilySymbol> pileTypes)
    {
        var gridThetaDeg = DetectGridTheta(doc, view);
        var place = PileFoundationPlacer.Place(doc, pileAssignments, boundaryRows, capLevel, capFloorType, embedmentMm, gridThetaDeg);

        // Dat lop lot cho moi dai da xu ly (moi lan cu), truoc khi bat tron vi Floor khong ap dung snap Grid theo diem.
        LotFloorSummary? lot = null;
        if (lotType is not null && capLevel is not null && place.ResolvedCapIds.Count > 0)
            lot = PileLotFloorPlacer.Place(doc, place.ResolvedCapIds, lotType, lotOffsetMm, capLevel, place.CreatedCapProfiles);

        SnapSummary? snap = null;
        JoinSummary? join = null;
        if (view is not null)
        {
            snap = PileGridSnapper.Snap(doc, view, place.CreatedPileIds.Concat(place.CreatedCapIds).ToList());
            join = PileCapJoiner.JoinPilesToFoundationSlabs(
                doc, view, PileTypeMatcher.FindPileFamilyNames(pileTypes), capFloorType?.Id, lotType?.Id);
        }

        return new PileWorkflowResult(place, lot, snap, join);
    }
}
