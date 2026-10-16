using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using View = Autodesk.Revit.DB.View;

namespace Bimtech.StructuralCore.Beams;

/// <summary>Gộp nhóm dầm (theo tên đọc từ Text nếu có, hoặc theo bề rộng b hình học nếu không) + đặt Family
/// Instance thật vào model, đúng vị trí tim dầm đọc từ CAD. Mỗi thao tác ghi có 1 hàm Plan* chỉ đọc model,
/// dùng chung hàm kiểm tra trùng/khớp Type nên xem trước ra đúng con số của thao tác ghi.</summary>
internal static class BeamPlacer
{
    private const double SnapUnitMm = 5.0;

    private const double DuplicatePositionToleranceMm = 10.0;
    private const double DuplicateAngleToleranceDegrees = 1.0;

    private const int MaxErrors = 20;

    /// <summary>Lựa chọn "không gán" trong ComboBox chọn Shared Parameter ở Bước 1 — chọn mục này thì tên đọc
    /// được từ Text (nếu có) vẫn dùng để gộp nhóm/auto-match Type, chỉ không ghi vào Parameter nào khi đặt.</summary>
    internal const string NoTagParameterLabel = "(Không gán Shared Parameter)";

    internal readonly record struct BeamLine(XYZ Start, XYZ End);

    /// <summary>Gộp dầm theo 3 loại: có TagName (gộp theo tên) — có TagHMm nhưng không tên (Text "bxh" trần, gộp
    /// theo cặp b×h) — không có Text nào (gộp theo b hình học). b luôn lấy từ hình học CAD, không lấy từ Text.
    /// Thứ tự: nhóm có tên trước (theo tên), rồi theo b.</summary>
    public static List<BeamSizeGroup> GroupBeams(IEnumerable<RawBeam> beams)
    {
        var beamList = beams.ToList();
        var groups = new List<BeamSizeGroup>();

        foreach (var byName in beamList
                     .Where(b => b.TagName is not null)
                     .GroupBy(b => b.TagName!, StringComparer.OrdinalIgnoreCase))
        {
            var first = byName.First();
            groups.Add(new BeamSizeGroup
            {
                TagName = byName.Key,
                SizeBMm = RoundTo5(UnitUtils.ConvertFromInternalUnits(first.WidthFeet, UnitTypeId.Millimeters)),
                SizeHMm = first.TagHMm!.Value,
                Beams = byName.ToList(),
            });
        }

        var byDim = new Dictionary<(long B, long H), (double SizeBMm, double SizeHMm, List<RawBeam> Beams)>();
        foreach (var beam in beamList.Where(b => b.TagName is null && b.TagHMm is not null))
        {
            var bMm = RoundTo5(UnitUtils.ConvertFromInternalUnits(beam.WidthFeet, UnitTypeId.Millimeters));
            var hMm = beam.TagHMm!.Value;
            var key = ((long)Math.Round(bMm * 100), (long)Math.Round(hMm * 100));

            if (!byDim.TryGetValue(key, out var dimEntry))
            {
                dimEntry = (bMm, hMm, new List<RawBeam>());
                byDim[key] = dimEntry;
            }

            dimEntry.Beams.Add(beam);
            byDim[key] = dimEntry;
        }

        foreach (var entry in byDim.Values.OrderBy(e => e.SizeBMm).ThenBy(e => e.SizeHMm))
            groups.Add(new BeamSizeGroup { SizeBMm = entry.SizeBMm, SizeHMm = entry.SizeHMm, Beams = entry.Beams });

        var byKey = new Dictionary<long, (double SizeBMm, List<RawBeam> Beams)>();
        foreach (var beam in beamList.Where(b => b.TagName is null && b.TagHMm is null))
        {
            var widthMm = UnitUtils.ConvertFromInternalUnits(beam.WidthFeet, UnitTypeId.Millimeters);
            var rounded = RoundTo5(widthMm);
            var key = (long)Math.Round(rounded * 100);

            if (!byKey.TryGetValue(key, out var entry))
            {
                entry = (rounded, new List<RawBeam>());
                byKey[key] = entry;
            }

            entry.Beams.Add(beam);
            byKey[key] = entry;
        }

        foreach (var entry in byKey.Values.OrderBy(e => e.SizeBMm))
            groups.Add(new BeamSizeGroup { SizeBMm = entry.SizeBMm, Beams = entry.Beams });

        return groups
            .OrderBy(g => g.TagName is null ? 1 : 0)
            .ThenBy(g => g.TagName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(g => g.SizeBMm)
            .ToList();
    }

    /// <summary>Type gợi ý cho 1 nhóm: khớp đúng b×h nếu có (IsExact = true), không thì gần nhất theo b×h, còn
    /// nhóm không biết h thì gần nhất theo b.</summary>
    public static (T? Choice, bool IsExact) SuggestType<T>(BeamSizeGroup group, IReadOnlyList<T> choices, Func<T, FamilySymbol> symbolOf)
        where T : class
    {
        if (group.SizeHMm is not double h)
            return (BeamTypeMatcher.AutoMatchByWidth(group.SizeBMm, choices, symbolOf), false);

        var exact = BeamTypeMatcher.FindExactMatch(group.SizeBMm, h, choices, symbolOf);
        return exact is not null
            ? (exact, true)
            : (BeamTypeMatcher.AutoMatchExact(group.SizeBMm, h, choices, symbolOf), false);
    }

    private static double RoundTo5(double valueMm) =>
        Math.Round(valueMm / SnapUnitMm, MidpointRounding.AwayFromZero) * SnapUnitMm;

    // ------------------------------------------------------------------ Tạo Type còn thiếu

    /// <summary>Xem trước bước tạo Type, không sửa model. Nhiều nhóm khác tên nhưng cùng b×h dùng chung 1 Type
    /// mới (giống thao tác ghi) — các nhóm sau có cùng NewTypeName với nhóm đầu.</summary>
    public static List<(BeamSizeGroup Group, FamilySymbol? Reuse, string? NewTypeName, string? Error)> PlanMissingTypes(
        Document doc, IEnumerable<BeamSizeGroup> groups, Family templateFamily)
    {
        var familySymbols = SymbolsOf(doc, templateFamily);
        var existingNames = familySymbols.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var planned = new List<(double B, double H, string Name)>();
        var previews = new List<(BeamSizeGroup, FamilySymbol?, string?, string?)>();

        foreach (var group in groups)
        {
            if (group.SizeHMm is not double hMm)
            {
                previews.Add((group, null, null, "chưa biết chiều cao h (không có Text), không tạo được Type"));
                continue;
            }

            var reuse = BeamTypeMatcher.FindExactMatch(group.SizeBMm, hMm, familySymbols, s => s);
            if (reuse is not null)
            {
                previews.Add((group, reuse, null, null));
                continue;
            }

            var shared = planned.FirstOrDefault(p =>
                Math.Abs(p.B - group.SizeBMm) <= BeamTypeMatcher.MatchToleranceMm && Math.Abs(p.H - hMm) <= BeamTypeMatcher.MatchToleranceMm);
            if (shared.Name is not null)
            {
                previews.Add((group, null, shared.Name, null));
                continue;
            }

            if (BeamTypeMatcher.AutoMatchExact(group.SizeBMm, hMm, familySymbols, s => s) is null)
            {
                previews.Add((group, null, null, $"Family mẫu \"{templateFamily.Name}\" không có Type chứa tham số b/h dạng Length."));
                continue;
            }

            var name = SymbolParameters.UniqueName($"{(long)Math.Round(group.SizeBMm)}x{(long)Math.Round(hMm)}", existingNames);
            existingNames.Add(name);
            planned.Add((group.SizeBMm, hMm, name));
            previews.Add((group, null, name, null));
        }

        return previews;
    }

    /// <summary>Duplicate Type mới + set b/h cho các nhóm cho trước (caller chỉ truyền nhóm CẦN DỰNG, CÓ h và CHƯA
    /// khớp đúng Type) — nhiều nhóm khác tên nhưng cùng kích thước dùng chung 1 Type vừa tạo/khớp trong cùng lượt gọi, không tạo trùng.</summary>
    public static BeamCreateTypesSummary CreateMissingTypes(Document doc, IEnumerable<BeamSizeGroup> groups, Family templateFamily)
    {
        var created = 0;
        var failed = 0;
        var errors = new List<string>();
        var createdSymbols = new List<FamilySymbol>();
        var resolved = new Dictionary<BeamSizeGroup, FamilySymbol>();
        var familySymbols = SymbolsOf(doc, templateFamily);

        using var tx = new Transaction(doc, "Tạo Type Dầm còn thiếu");
        tx.Start();

        foreach (var group in groups)
        {
            if (group.SizeHMm is not double hMm)
                continue;

            var reuse = BeamTypeMatcher.FindExactMatch(group.SizeBMm, hMm, familySymbols, s => s);
            if (reuse is not null)
            {
                resolved[group] = reuse;
                continue;
            }

            var template = BeamTypeMatcher.AutoMatchExact(group.SizeBMm, hMm, familySymbols, s => s);
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
            var newName = SymbolParameters.UniqueName($"{(long)Math.Round(group.SizeBMm)}x{(long)Math.Round(hMm)}", existingNames);

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
                errors.Add($"Dầm {group.SizeLabel}: lỗi khi tạo Type — {ex.Message}");
                continue;
            }

            var bParam = newSymbol.LookupParameter(BeamTypeMatcher.BeamBParam);
            var hParam = newSymbol.LookupParameter(BeamTypeMatcher.BeamHParam);

            if (bParam is null || hParam is null || bParam.StorageType != StorageType.Double || hParam.StorageType != StorageType.Double)
            {
                failed++;
                errors.Add($"Family \"{family.Name}\" không có tham số b/h dạng Length — Type \"{newName}\" đã tạo nhưng KHÔNG đúng kích thước, kiểm tra lại family.");
                continue;
            }

            bParam.Set(UnitUtils.ConvertToInternalUnits(group.SizeBMm, UnitTypeId.Millimeters));
            hParam.Set(UnitUtils.ConvertToInternalUnits(hMm, UnitTypeId.Millimeters));

            created++;
            createdSymbols.Add(newSymbol);
            familySymbols.Add(newSymbol);
            resolved[group] = newSymbol;
        }

        tx.Commit();
        return new BeamCreateTypesSummary(created, failed, errors, createdSymbols, resolved);
    }

    /// <summary>Type dầm đúng b×h trong Family mẫu, trong Transaction do caller mở sẵn: dùng lại nếu có (kể cả Type vừa tạo
    /// ở mục trước), không thì duplicate Type gần nhất, tên "b×h", set b/h. Ném InvalidOperationException khi Family không
    /// có tham số b/h dạng Length sửa được.</summary>
    public static (FamilySymbol Symbol, bool Created) ResolveOrCreateBeamType(Document doc, double bMm, double hMm, Family family)
    {
        var symbols = SymbolsOf(doc, family);
        if (BeamTypeMatcher.FindExactMatch(bMm, hMm, symbols, s => s) is { } existing)
            return (existing, false);
        var template = BeamTypeMatcher.AutoMatchExact(bMm, hMm, symbols, s => s)
            ?? throw new InvalidOperationException($"Family \"{family.Name}\" không có Type chứa tham số b/h dạng Length.");
        var name = SymbolParameters.UniqueName($"{(long)Math.Round(bMm)}x{(long)Math.Round(hMm)}",
            symbols.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
        if (template.Duplicate(name) is not FamilySymbol created)
            throw new InvalidOperationException("Duplicate không trả về FamilySymbol.");
        var b = created.LookupParameter(BeamTypeMatcher.BeamBParam);
        var h = created.LookupParameter(BeamTypeMatcher.BeamHParam);
        if (b is not { StorageType: StorageType.Double, IsReadOnly: false } || h is not { StorageType: StorageType.Double, IsReadOnly: false })
            throw new InvalidOperationException($"Family \"{family.Name}\" không có tham số b/h dạng Length sửa được.");
        b.Set(UnitUtils.ConvertToInternalUnits(bMm, UnitTypeId.Millimeters));
        h.Set(UnitUtils.ConvertToInternalUnits(hMm, UnitTypeId.Millimeters));
        return (created, true);
    }

    /// <summary>Đặt 1 dầm từ start tới end (Z đã gồm cao độ đỉnh dầm), trong Transaction do caller mở sẵn; ghi mark vào
    /// Mark nếu có. Ném InvalidOperationException khi Revit không tạo được.</summary>
    public static ElementId PlaceBeamInTransaction(Document doc, XYZ start, XYZ end, FamilySymbol symbol, Level level, string? mark)
    {
        if (!symbol.IsActive) symbol.Activate();
        var instance = doc.Create.NewFamilyInstance(Line.CreateBound(start, end), symbol, level, StructuralType.Beam);
        if (mark is not null) instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(mark);
        return instance.Id;
    }

    private static List<FamilySymbol> SymbolsOf(Document doc, Family family) =>
        family.GetFamilySymbolIds().Select(doc.GetElement).OfType<FamilySymbol>().ToList();

    // ------------------------------------------------------------------ Đặt dầm

    /// <summary>Xem trước bước Đặt, không sửa model: mỗi nhóm bao nhiêu dầm sẽ tạo, bao nhiêu trùng dầm đã có.
    /// Dầm sắp tạo trong cùng lượt cũng được tính vào kiểm tra trùng, giống thao tác ghi.</summary>
    public static List<(BeamSizeGroup Group, int ToCreate, int Duplicates, string? Problem)> PlanPlacement(
        Document doc, IReadOnlyList<BeamAssignment> assignments, Level? level)
    {
        var existingLines = CollectExistingBeamLines(doc);
        var previews = new List<(BeamSizeGroup, int, int, string?)>();

        foreach (var (group, symbol) in assignments)
        {
            if (symbol is null || level is null)
            {
                previews.Add((group, 0, 0, "chưa gán đủ Type/Level"));
                continue;
            }

            var toCreate = 0;
            var duplicates = 0;
            foreach (var beam in group.Beams)
            {
                var candidate = LineAt(beam, level);
                if (existingLines.Any(existing => AreDuplicate(existing, candidate)))
                {
                    duplicates++;
                    continue;
                }
                toCreate++;
                existingLines.Add(candidate);
            }
            previews.Add((group, toCreate, duplicates, null));
        }

        return previews;
    }

    /// <summary>Đặt toàn bộ dầm của các nhóm cho trước (caller chỉ truyền nhóm cần dựng) vào model trong 1
    /// Transaction, bỏ qua vị trí đã có sẵn (chống trùng). Dầm đặt thẳng theo Level tham chiếu, offset = 0.
    /// <paramref name="tagParameterName"/> — Shared Parameter để ghi TagName của từng nhóm vào — null/rỗng thì
    /// không ghi gì (nhóm vẫn gộp/auto-match theo tên đọc được như bình thường).</summary>
    public static PlaceSummary Place(Document doc, IReadOnlyList<BeamAssignment> assignments, Level? level, string? tagParameterName)
    {
        var created = 0;
        var failed = 0;
        var skipped = 0;
        var errors = new List<string>();
        var createdIds = new List<ElementId>();

        var existingLines = CollectExistingBeamLines(doc);

        using var tx = new Transaction(doc, "Đặt Dầm theo CAD");
        tx.Start();

        foreach (var (group, symbol) in assignments)
        {
            if (symbol is null || level is null)
            {
                failed += group.Beams.Count;
                if (errors.Count < MaxErrors)
                    errors.Add($"Dầm {group.SizeLabel}: chưa gán đủ Type/Level, bỏ qua toàn bộ nhóm.");
                continue;
            }

            foreach (var beam in group.Beams)
            {
                var candidate = LineAt(beam, level);
                if (existingLines.Any(existing => AreDuplicate(existing, candidate)))
                {
                    skipped++;
                    continue;
                }

                var result = PlaceOne(doc, beam, symbol, level, tagParameterName, group.TagName);
                if (result.Outcome == PlaceOutcome.Created)
                {
                    created++;
                    existingLines.Add(candidate);
                    if (result.CreatedId is not null)
                        createdIds.Add(result.CreatedId);
                }
                else
                {
                    failed++;
                    if (result.ErrorMessage is not null && errors.Count < MaxErrors)
                        errors.Add($"Dầm {group.SizeLabel}: {result.ErrorMessage}");
                }
            }
        }

        tx.Commit();

        return new PlaceSummary(created, failed, skipped, errors, createdIds);
    }

    private static BeamLine LineAt(RawBeam beam, Level level) => new(
        new XYZ(beam.Start.X, beam.Start.Y, level.Elevation),
        new XYZ(beam.End.X, beam.End.Y, level.Elevation));

    internal static List<BeamLine> CollectExistingBeamLines(Document doc)
    {
        var lines = new List<BeamLine>();
        var beams = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_StructuralFraming)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .Where(fi => fi.StructuralType == StructuralType.Beam);

        foreach (var beam in beams)
        {
            if (beam.Location is not LocationCurve { Curve: Line line })
                continue;
            lines.Add(new BeamLine(line.GetEndPoint(0), line.GetEndPoint(1)));
        }

        return lines;
    }

    internal static bool AreDuplicate(BeamLine existing, BeamLine candidate)
    {
        var toleranceFeet = UnitUtils.ConvertToInternalUnits(DuplicatePositionToleranceMm, UnitTypeId.Millimeters);
        if (Math.Abs(existing.Start.Z - candidate.Start.Z) > toleranceFeet ||
            Math.Abs(existing.End.Z - candidate.End.Z) > toleranceFeet)
            return false;

        var existingVector = new XYZ(existing.End.X - existing.Start.X, existing.End.Y - existing.Start.Y, 0);
        var candidateVector = new XYZ(candidate.End.X - candidate.Start.X, candidate.End.Y - candidate.Start.Y, 0);
        var existingLength = existingVector.GetLength();
        var candidateLength = candidateVector.GetLength();
        if (existingLength <= toleranceFeet || candidateLength <= toleranceFeet)
            return false;

        var existingDirection = existingVector / existingLength;
        var candidateDirection = candidateVector / candidateLength;
        var cross = Math.Abs(existingDirection.X * candidateDirection.Y - existingDirection.Y * candidateDirection.X);
        if (cross > Math.Sin(DuplicateAngleToleranceDegrees * Math.PI / 180.0))
            return false;

        double DistanceToExistingAxis(XYZ point)
        {
            var offsetX = point.X - existing.Start.X;
            var offsetY = point.Y - existing.Start.Y;
            return Math.Abs(offsetX * existingDirection.Y - offsetY * existingDirection.X);
        }

        if (DistanceToExistingAxis(candidate.Start) > toleranceFeet ||
            DistanceToExistingAxis(candidate.End) > toleranceFeet)
            return false;

        double ProjectOnExistingAxis(XYZ point) =>
            (point.X - existing.Start.X) * existingDirection.X +
            (point.Y - existing.Start.Y) * existingDirection.Y;

        var candidateMin = Math.Min(ProjectOnExistingAxis(candidate.Start), ProjectOnExistingAxis(candidate.End));
        var candidateMax = Math.Max(ProjectOnExistingAxis(candidate.Start), ProjectOnExistingAxis(candidate.End));
        var overlap = Math.Min(existingLength, candidateMax) - Math.Max(0.0, candidateMin);
        return overlap > toleranceFeet;
    }

    /// <summary>Đặt 1 Family Instance Dầm nối 2 đầu tim đọc từ CAD, theo đúng Level tham chiếu (offset = 0).
    /// Ghi <paramref name="tagValue"/> (TagName của nhóm) vào Parameter tên <paramref name="tagParameterName"/>
    /// nếu cả 2 đều có giá trị — lỗi/thiếu Parameter khi ghi KHÔNG làm hỏng việc đặt dầm (bỏ qua âm thầm),
    /// vì dầm đã tạo thành công là phần việc chính, gán tên chỉ là phụ trợ.</summary>
    private static PlaceResult PlaceOne(
        Document doc, RawBeam beam, FamilySymbol symbol, Level level,
        string? tagParameterName, string? tagValue)
    {
        try
        {
            if (!symbol.IsActive)
                symbol.Activate();

            var start = new XYZ(beam.Start.X, beam.Start.Y, level.Elevation);
            var end = new XYZ(beam.End.X, beam.End.Y, level.Elevation);
            var curve = Line.CreateBound(start, end);

            var instance = doc.Create.NewFamilyInstance(curve, symbol, level, StructuralType.Beam);

            if (!string.IsNullOrEmpty(tagParameterName) && !string.IsNullOrEmpty(tagValue))
            {
                var tagParam = instance.LookupParameter(tagParameterName);
                if (tagParam is { IsReadOnly: false, StorageType: StorageType.String })
                    tagParam.Set(tagValue);
            }

            return new PlaceResult(PlaceOutcome.Created, CreatedId: instance.Id);
        }
        catch (Exception ex)
        {
            return new PlaceResult(PlaceOutcome.Failed, ex.Message);
        }
    }
}

/// <summary>Quét dầm đầy đủ như tool WPF: ghép cặp Line, sửa Line mồ côi, rồi gán tên/kích thước từ Text Note
/// trong view. Dùng chung cho PlaceBeam và MCP để hai nơi luôn ra cùng kết quả.</summary>
internal static class BeamScanPipeline
{
    public const double DefaultTextSearchExpandMm = BeamTagReader.DefaultSearchExpandMm;
    public const double DefaultColumnSearchDistanceMm = 3000.0;

    public static CadBeamScanResult ScanWithTags(Document doc, ImportInstance cad, View view, double textSearchExpandFeet)
    {
        var textNotes = new FilteredElementCollector(doc, view.Id).OfClass(typeof(TextNote)).Cast<TextNote>().ToList();
        var scan = CadBeamScanner.Scan(doc, cad, view);

        var repaired = BeamOrphanRepairer.Repair(scan.RawSegmentsByLayer, scan.Orphans);
        foreach (var (beam, layerName) in repaired)
        {
            if (scan.BeamsByLayer.TryGetValue(layerName, out var list))
                list.Add(beam);
        }

        BeamTagReader.AttachTags(scan.BeamsByLayer.Values.SelectMany(list => list), textNotes, textSearchExpandFeet);
        return scan;
    }
}
