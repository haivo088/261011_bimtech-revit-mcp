using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace Bimtech.StructuralCore.Columns;

/// <summary>Gộp nhóm cột theo kích thước b×h + đặt Family Instance thật vào model, đúng góc xoay CAD.
/// Mỗi thao tác ghi có 1 hàm Plan* tương ứng chỉ đọc model — trả về đúng con số mà thao tác ghi sẽ tạo ra
/// (dùng cho dry-run), vì cả hai dùng chung các hàm kiểm tra trùng/khớp Type bên dưới.</summary>
internal static class ColumnPlacer
{
    private const double SnapUnitMm = 5.0;

    /// <summary>Ngưỡng mở rộng bounding box (X/Y) khi chống đặt trùng.</summary>
    private const double DuplicateExpandMm = 5.0;

    /// <summary>Ngưỡng chồng lấn Z tối thiểu để coi 2 cột khác tầng là trùng — 2 khoảng Z chỉ CHẠM đúng ranh
    /// giới Level (vd đáy cột tầng 2 = đỉnh cột tầng 1, chênh lệch = 0) KHÔNG bị tính là chồng lấn.</summary>
    private const double DuplicateZOverlapTolMm = 10.0;

    private const int MaxErrors = 20;

    /// <summary>1 hộp 3D (X/Y đã mở rộng, Z KHÔNG mở rộng) của 1 element cũ, dùng chống trùng — X/Y xác định
    /// cùng lưới mặt bằng, Z xác định có thật sự chồng lấn cao độ hay chỉ là cột ở tầng khác cùng lưới.</summary>
    internal readonly record struct PlanBox(double MinX, double MaxX, double MinY, double MaxY, double MinZ, double MaxZ)
    {
        public bool Contains(XYZ point) => point.X >= MinX && point.X <= MaxX && point.Y >= MinY && point.Y <= MaxY;

        /// <summary>Phần chồng lấn Z (feet) với khoảng [testMinZ, testMaxZ] cho trước — âm/0 nghĩa là không chồng lấn (chỉ chạm hoặc tách rời).</summary>
        public double OverlapZFeet(double testMinZ, double testMaxZ) =>
            Math.Min(MaxZ, testMaxZ) - Math.Max(MinZ, testMinZ);
    }

    /// <summary>Gộp cột theo (ký hiệu, kích thước b×h làm tròn bội 5mm), không phân biệt hướng xoay. <paramref name="tagOf"/>:
    /// ký hiệu đọc từ Text Note của từng cột (null nếu không có). 1 ký hiệu có nhiều kích thước thì mỗi kích thước 1 nhóm,
    /// khoá "C1 300x500". Nhóm có ký hiệu trước, rồi theo kích thước.</summary>
    public static List<ColumnSizeGroup> GroupColumns(IEnumerable<RawColumn> columns, Func<RawColumn, string?>? tagOf = null)
    {
        var byKey = new Dictionary<(string? Tag, long A, long B), (double SizeAMm, double SizeBMm, List<RawColumn> Columns)>();

        foreach (var column in columns)
        {
            var lengthMm = UnitUtils.ConvertFromInternalUnits(column.LengthFeet, UnitTypeId.Millimeters);
            var widthMm = UnitUtils.ConvertFromInternalUnits(column.WidthFeet, UnitTypeId.Millimeters);
            var roundedLength = RoundTo5(lengthMm);
            var roundedWidth = RoundTo5(widthMm);
            var sizeA = Math.Min(roundedLength, roundedWidth);
            var sizeB = Math.Max(roundedLength, roundedWidth);
            var key = (tagOf?.Invoke(column), (long)Math.Round(sizeA * 100), (long)Math.Round(sizeB * 100));

            if (!byKey.TryGetValue(key, out var entry))
            {
                entry = (sizeA, sizeB, new List<RawColumn>());
                byKey[key] = entry;
            }

            entry.Columns.Add(column);
        }

        var sizesPerTag = byKey.Keys.Where(k => k.Tag is not null).GroupBy(k => k.Tag!).ToDictionary(g => g.Key, g => g.Count());
        return byKey
            .OrderBy(e => e.Key.Tag is null).ThenBy(e => e.Key.Tag, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Value.SizeAMm).ThenBy(e => e.Value.SizeBMm)
            .Select(e => new ColumnSizeGroup
            {
                SizeAMm = e.Value.SizeAMm,
                SizeBMm = e.Value.SizeBMm,
                Columns = e.Value.Columns,
                TagName = e.Key.Tag,
                KeyOverride = e.Key.Tag is { } tag && sizesPerTag[tag] > 1 ? $"{tag} {e.Value.SizeAMm:0}x{e.Value.SizeBMm:0}" : null,
            })
            .ToList();
    }

    // ------------------------------------------------------------------ Dùng trong Transaction do caller mở sẵn (MCP)

    /// <summary>Đặt 1 cột giữa 2 Level. Ném InvalidOperationException khi Revit không đặt được.</summary>
    public static ElementId PlaceColumnInTransaction(Document doc, RawColumn column, FamilySymbol symbol, Level baseLevel, Level topLevel)
    {
        var result = PlaceOne(doc, column, symbol, baseLevel, topLevel);
        return result.CreatedId ?? throw new InvalidOperationException(result.ErrorMessage ?? "Không đặt được cột.");
    }

    /// <summary>Type cột đúng b×h trong Family mẫu, trong Transaction do caller mở sẵn: dùng lại nếu có (kể cả Type vừa tạo ở
    /// mục trước), không thì duplicate Type gần nhất của Family, tên "b×h", set b/h. Ném InvalidOperationException khi Family
    /// không có tham số b/h dạng Length.</summary>
    public static (FamilySymbol Symbol, bool Created) ResolveOrCreateColumnType(Document doc, double sizeAMm, double sizeBMm, Family family)
    {
        var symbols = SymbolsOf(doc, family);
        if (ColumnTypeMatcher.FindExactMatch(sizeAMm, sizeBMm, symbols, s => s) is { } existing)
            return (existing, false);
        var template = ColumnTypeMatcher.AutoMatch(sizeAMm, sizeBMm, symbols, s => s)
            ?? throw new InvalidOperationException($"Family \"{family.Name}\" không có Type chứa tham số b/h dạng Length.");

        var group = new ColumnSizeGroup { SizeAMm = sizeAMm, SizeBMm = sizeBMm, Columns = [] };
        var name = UniqueTypeName(group, symbols.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
        if (template.Duplicate(name) is not FamilySymbol created)
            throw new InvalidOperationException("Duplicate không trả về FamilySymbol.");
        var b = created.LookupParameter(ColumnTypeMatcher.ColumnBParam);
        var h = created.LookupParameter(ColumnTypeMatcher.ColumnHParam);
        if (b is not { StorageType: StorageType.Double, IsReadOnly: false } || h is not { StorageType: StorageType.Double, IsReadOnly: false })
            throw new InvalidOperationException($"Family \"{family.Name}\" không có tham số b/h dạng Length sửa được.");
        b.Set(UnitUtils.ConvertToInternalUnits(sizeAMm, UnitTypeId.Millimeters));
        h.Set(UnitUtils.ConvertToInternalUnits(sizeBMm, UnitTypeId.Millimeters));
        return (created, true);
    }

    private static double RoundTo5(double valueMm) =>
        Math.Round(valueMm / SnapUnitMm, MidpointRounding.AwayFromZero) * SnapUnitMm;

    // ------------------------------------------------------------------ Đặt cột

    /// <summary>Xem trước bước Đặt, không sửa model: mỗi nhóm bao nhiêu cột sẽ tạo, bao nhiêu bị bỏ qua vì đã
    /// có cột cùng Family tại đó, nhóm nào thiếu Type/Level.</summary>
    public static List<GroupPlacementPreview> PlanPlacement(
        Document doc, IReadOnlyList<ColumnAssignment> assignments, Level? baseLevel, Level? topLevel)
    {
        var duplicateCheck = new DuplicateChecker(doc, baseLevel, topLevel);
        var previews = new List<GroupPlacementPreview>();

        foreach (var (group, symbol) in assignments)
        {
            if (symbol is null || baseLevel is null || topLevel is null)
            {
                previews.Add(new GroupPlacementPreview(group, symbol, 0, 0, "chưa gán đủ Type/Level"));
                continue;
            }

            var duplicates = group.Columns.Count(column => duplicateCheck.Find(column, symbol) is not null);
            previews.Add(new GroupPlacementPreview(group, symbol, group.Columns.Count - duplicates, duplicates, null));
        }

        return previews;
    }

    /// <summary>Đặt toàn bộ cột vào model trong 1 Transaction, bỏ qua vị trí đã có sẵn (chống trùng).</summary>
    public static PlaceSummary Place(Document doc, IReadOnlyList<ColumnAssignment> assignments, Level? baseLevel, Level? topLevel)
    {
        var created = 0;
        var failed = 0;
        var skipped = 0;
        var errors = new List<string>();
        var createdIds = new List<ElementId>();

        var duplicateCheck = new DuplicateChecker(doc, baseLevel, topLevel);

        using var tx = new Transaction(doc, "Đặt Cột theo CAD");
        tx.Start();

        foreach (var (group, symbol) in assignments)
        {
            if (symbol is null || baseLevel is null || topLevel is null)
            {
                failed += group.Columns.Count;
                if (errors.Count < MaxErrors)
                    errors.Add($"Cột {group.SizeLabel}: chưa gán đủ Type/Level, bỏ qua toàn bộ nhóm.");
                continue;
            }

            foreach (var column in group.Columns)
            {
                if (duplicateCheck.Find(column, symbol) is not null)
                {
                    skipped++;
                    continue;
                }

                var result = PlaceOne(doc, column, symbol, baseLevel, topLevel);
                if (result.Outcome == PlaceOutcome.Created)
                {
                    created++;
                    if (result.CreatedId is not null)
                        createdIds.Add(result.CreatedId);
                }
                else
                {
                    failed++;
                    if (result.ErrorMessage is not null && errors.Count < MaxErrors)
                        errors.Add($"Cột {group.SizeLabel}: {result.ErrorMessage}");
                }
            }
        }

        tx.Commit();

        return new PlaceSummary(created, failed, skipped, errors, createdIds);
    }

    /// <summary>Chống đặt trùng: hộp bao MỌI cột kết cấu có sẵn trong model (mọi Family — cột của family khác ở đúng chỗ đó
    /// cũng là trùng), đọc 1 lần. Cột vừa đặt trong cùng lượt không được thêm vào.</summary>
    internal sealed class DuplicateChecker(Document doc, Level? baseLevel, Level? topLevel)
    {
        private List<(PlanBox Box, ElementId Id)>? _boxes;

        // 1 luot Dat luon dung dung 1 cap Level day/dinh cho moi nhom - dung chung de phan biet cot cung
        // luoi X/Y nhung khac tang (xem PlanBox/FindDuplicate).
        private readonly double _testMinZ = baseLevel?.Elevation ?? 0.0;
        private readonly double _testMaxZ = topLevel?.Elevation ?? 0.0;

        public ElementId? Find(RawColumn column, FamilySymbol symbol) => Find(column.Center);

        public ElementId? Find(XYZ center)
        {
            _boxes ??= BuildExistingBoxes(new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_StructuralColumns)
                .OfClass(typeof(FamilyInstance)), DuplicateExpandMm);
            return FindDuplicate(center, _boxes, _testMinZ, _testMaxZ);
        }
    }

    /// <summary>Bounding box (X/Y, đã mở rộng expandMm) của các element cho trước — dùng chung cho cơ chế chống trùng.</summary>
    internal static List<(PlanBox Box, ElementId Id)> BuildExistingBoxes(IEnumerable<Element> elements, double expandMm)
    {
        var expandFeet = UnitUtils.ConvertToInternalUnits(expandMm, UnitTypeId.Millimeters);
        var boxes = new List<(PlanBox, ElementId)>();

        foreach (var element in elements)
        {
            BoundingBoxXYZ? bb;
            try
            {
                bb = element.get_BoundingBox(null);
            }
            catch (Exception)
            {
                bb = null;
            }

            if (bb is null)
                continue;

            // Z KHONG mo rong theo expandMm - nguong mo rong 5mm chi ap dung cho X/Y (chong dat trung tren
            // cung mat bang), chieu Z dung nguong chong lan rieng o FindDuplicate (DuplicateZOverlapTolMm).
            boxes.Add((
                new PlanBox(bb.Min.X - expandFeet, bb.Max.X + expandFeet, bb.Min.Y - expandFeet, bb.Max.Y + expandFeet, bb.Min.Z, bb.Max.Z),
                element.Id));
        }

        return boxes;
    }

    /// <summary>Tìm ElementId của box đã có chứa điểm cho trước VÀ thật sự chồng lấn cao độ với [testMinZ, testMaxZ]
    /// (cột khác tầng cùng lưới X/Y không bị coi là trùng) — null nếu không trùng box nào.</summary>
    internal static ElementId? FindDuplicate(XYZ point, List<(PlanBox Box, ElementId Id)> boxes, double testMinZ, double testMaxZ)
    {
        var tolFeet = UnitUtils.ConvertToInternalUnits(DuplicateZOverlapTolMm, UnitTypeId.Millimeters);

        foreach (var (box, id) in boxes)
        {
            if (box.Contains(point) && box.OverlapZFeet(testMinZ, testMaxZ) > tolFeet)
                return id;
        }

        return null;
    }

    /// <summary>Đặt 1 Family Instance Cột giữa Level đáy/đỉnh, xoay đúng góc CAD dựa trên (b, h) của Type đã chọn.</summary>
    private static PlaceResult PlaceOne(Document doc, RawColumn column, FamilySymbol symbol, Level baseLevel, Level topLevel)
    {
        try
        {
            if (!symbol.IsActive)
                symbol.Activate();

            var point = new XYZ(column.Center.X, column.Center.Y, baseLevel.Elevation);
            var instance = doc.Create.NewFamilyInstance(point, symbol, baseLevel, StructuralType.Column);

            instance.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM)?.Set(baseLevel.Id);
            instance.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM)?.Set(topLevel.Id);
            instance.get_Parameter(BuiltInParameter.SCHEDULE_BASE_LEVEL_OFFSET_PARAM)?.Set(0.0);
            instance.get_Parameter(BuiltInParameter.SCHEDULE_TOP_LEVEL_OFFSET_PARAM)?.Set(0.0);

            var rotation = DetermineRotation(column, symbol);
            if (Math.Abs(rotation) > 1e-6)
            {
                var axis = Line.CreateBound(point, point + XYZ.BasisZ);
                ElementTransformUtils.RotateElement(doc, instance.Id, axis, rotation);
            }

            return new PlaceResult(PlaceOutcome.Created, CreatedId: instance.Id);
        }
        catch (Exception ex)
        {
            return new PlaceResult(PlaceOutcome.Failed, ex.Message);
        }
    }

    /// <summary>Chọn xoay trục b (X cục bộ) hay trục h (Y cục bộ) của Type khớp với trục chính CAD (AngleRadians)
    /// — so (b,h) của Type với (Length,Width) đo được của chính cột này, không dùng chung 1 hướng cho cả nhóm.</summary>
    private static double DetermineRotation(RawColumn column, FamilySymbol symbol)
    {
        var bMm = ColumnTypeMatcher.ReadLengthMm(symbol, ColumnTypeMatcher.ColumnBParam);
        var hMm = ColumnTypeMatcher.ReadLengthMm(symbol, ColumnTypeMatcher.ColumnHParam);
        if (bMm is null || hMm is null)
            return column.AngleRadians;

        var lengthMm = UnitUtils.ConvertFromInternalUnits(column.LengthFeet, UnitTypeId.Millimeters);
        var widthMm = UnitUtils.ConvertFromInternalUnits(column.WidthFeet, UnitTypeId.Millimeters);

        var diffStraight = Math.Abs(bMm.Value - lengthMm) + Math.Abs(hMm.Value - widthMm);
        var diffSwapped = Math.Abs(bMm.Value - widthMm) + Math.Abs(hMm.Value - lengthMm);

        return diffStraight <= diffSwapped ? column.AngleRadians : column.AngleRadians - Math.PI / 2;
    }

    // ------------------------------------------------------------------ Tạo Type còn thiếu

    /// <summary>Xem trước bước tạo Type, không sửa model: nhóm nào dùng lại Type có sẵn trong Family mẫu, nhóm
    /// nào sẽ tạo Type mới (kèm tên dự kiến), nhóm nào lỗi.</summary>
    public static List<TypeCreationPreview> PlanMissingTypes(Document doc, IEnumerable<ColumnSizeGroup> groups, Family templateFamily)
    {
        var familySymbols = SymbolsOf(doc, templateFamily);
        var existingNames = familySymbols.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var previews = new List<TypeCreationPreview>();

        foreach (var group in groups)
        {
            var reuse = ColumnTypeMatcher.FindExactMatch(group.SizeAMm, group.SizeBMm, familySymbols, s => s);
            if (reuse is not null)
            {
                previews.Add(new TypeCreationPreview(group, reuse, null, null));
                continue;
            }

            if (ColumnTypeMatcher.AutoMatch(group.SizeAMm, group.SizeBMm, familySymbols, s => s) is null)
            {
                previews.Add(new TypeCreationPreview(group, null, null,
                    $"Family mẫu \"{templateFamily.Name}\" không có Type chứa tham số b/h dạng Length."));
                continue;
            }

            var newName = UniqueTypeName(group, existingNames);
            existingNames.Add(newName);
            previews.Add(new TypeCreationPreview(group, null, newName, null));
        }

        return previews;
    }

    /// <summary>Tạo Type còn thiếu trong Family mẫu cho các nhóm cho trước (caller chỉ truyền nhóm CHƯA khớp
    /// đúng Type): dùng lại Type cùng kích thước nếu Family đã có, không thì duplicate Type gần nhất rồi set b/h.</summary>
    public static CreateTypesSummary CreateMissingTypes(Document doc, IEnumerable<ColumnSizeGroup> groups, Family templateFamily)
    {
        var created = 0;
        var failed = 0;
        var errors = new List<string>();
        var createdSymbols = new List<FamilySymbol>();
        var resolved = new Dictionary<ColumnSizeGroup, FamilySymbol>();
        var familySymbols = SymbolsOf(doc, templateFamily);

        using var tx = new Transaction(doc, "Tạo Type Cột còn thiếu");
        tx.Start();

        foreach (var group in groups)
        {
            var reuse = ColumnTypeMatcher.FindExactMatch(group.SizeAMm, group.SizeBMm, familySymbols, s => s);
            if (reuse is not null)
            {
                resolved[group] = reuse;
                continue;
            }

            var template = ColumnTypeMatcher.AutoMatch(group.SizeAMm, group.SizeBMm, familySymbols, s => s);
            if (template is null)
            {
                failed++;
                errors.Add($"Family mẫu \"{templateFamily.Name}\" không có Type chứa tham số b/h dạng Length.");
                continue;
            }

            var family = template.Family;
            var existingNames = family.GetFamilySymbolIds()
                .Select(id => doc.GetElement(id)?.Name)
                .Where(n => n is not null)
                .Select(n => n!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var newName = UniqueTypeName(group, existingNames);

            FamilySymbol newSymbol;
            try
            {
                if (template.Duplicate(newName) is not FamilySymbol duplicated)
                    throw new InvalidOperationException("Duplicate không trả về FamilySymbol.");
                newSymbol = duplicated;
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"Cột {group.SizeLabel}: lỗi khi tạo Type — {ex.Message}");
                continue;
            }

            var bParam = newSymbol.LookupParameter(ColumnTypeMatcher.ColumnBParam);
            var hParam = newSymbol.LookupParameter(ColumnTypeMatcher.ColumnHParam);

            if (bParam is null || hParam is null || bParam.StorageType != StorageType.Double || hParam.StorageType != StorageType.Double)
            {
                failed++;
                errors.Add($"Family \"{family.Name}\" không có tham số b/h dạng Length — Type \"{newName}\" đã tạo nhưng KHÔNG đúng kích thước, kiểm tra lại family.");
                continue;
            }

            bParam.Set(UnitUtils.ConvertToInternalUnits(group.SizeAMm, UnitTypeId.Millimeters));
            hParam.Set(UnitUtils.ConvertToInternalUnits(group.SizeBMm, UnitTypeId.Millimeters));

            created++;
            createdSymbols.Add(newSymbol);
            familySymbols.Add(newSymbol);
            resolved[group] = newSymbol;
        }

        tx.Commit();
        return new CreateTypesSummary(created, failed, errors, createdSymbols, resolved);
    }

    private static List<FamilySymbol> SymbolsOf(Document doc, Family family) =>
        family.GetFamilySymbolIds()
            .Select(doc.GetElement)
            .OfType<FamilySymbol>()
            .ToList();

    private static string UniqueTypeName(ColumnSizeGroup group, HashSet<string> existingNames)
    {
        var baseName = $"{(long)Math.Round(group.SizeAMm)}x{(long)Math.Round(group.SizeBMm)}";
        var newName = baseName;
        for (var suffix = 2; existingNames.Contains(newName); suffix++)
            newName = $"{baseName} ({suffix})";
        return newName;
    }
}

/// <summary>Xem trước bước Đặt cho 1 nhóm. Problem khác null nghĩa là cả nhóm sẽ bị bỏ qua.</summary>
internal sealed record GroupPlacementPreview(
    ColumnSizeGroup Group, FamilySymbol? Symbol, int ToCreate, int Duplicates, string? Problem);

/// <summary>Xem trước bước tạo Type cho 1 nhóm: Reuse (dùng lại), NewTypeName (sẽ tạo) hoặc Error.</summary>
internal sealed record TypeCreationPreview(
    ColumnSizeGroup Group, FamilySymbol? Reuse, string? NewTypeName, string? Error);
