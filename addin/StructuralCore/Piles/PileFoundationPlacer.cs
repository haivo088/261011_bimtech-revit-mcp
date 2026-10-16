using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace Bimtech.StructuralCore.Piles;

/// <summary>Gộp nhóm cọc theo đường kính + đặt cọc (FamilyInstance) và đài (Foundation Slab) thật vào model.
/// Mỗi thao tác ghi có 1 hàm Plan* chỉ đọc model, đi đúng thứ tự kiểm tra của thao tác ghi.</summary>
internal static class PileFoundationPlacer
{
    private const double SnapUnitMm = 5.0;

    /// <summary>Ngưỡng mở rộng bounding box (X/Y) khi chống đặt trùng.</summary>
    private const double DuplicateExpandMm = 5.0;

    /// <summary>Tên tham số family cọc quyết định chiều cao cọc ăn vào đài — set trực tiếp vào biến này thay vì cộng dồn vào cao độ đặt cọc.</summary>
    private const string PileEmbedmentParam = "Minimum Embedment";

    /// <summary>Ngưỡng coi 2 circle là cùng 1 tim cọc (CAD hay vẽ lồng 2 vòng tròn cho 1 cọc).</summary>
    private const double ConcentricTolMm = 20.0;

    /// <summary>Ngưỡng coi 2 đường bao là cùng 1 đài (CAD hay vẽ lồng 2 đường bao cho 1 đài — vd viền ván khuôn + viền bê tông thực).</summary>
    private const double BoundaryDuplicateTolMm = 200.0;

    public const double DefaultEmbedmentMm = 100.0;

    private const int MaxErrors = 20;

    /// <summary>1 vùng chữ nhật mặt bằng (X/Y) đã mở rộng của 1 element cũ, dùng chống trùng.</summary>
    internal readonly record struct PlanBox(double MinX, double MaxX, double MinY, double MaxY)
    {
        public bool Contains(XYZ point) => point.X >= MinX && point.X <= MaxX && point.Y >= MinY && point.Y <= MaxY;
    }

    /// <summary>Gộp circle theo bán kính đã làm tròn bội 5mm, so bằng đúng qua key nguyên hoá. <paramref name="pileTypes"/>
    /// chỉ dùng để chọn circle đại diện khi CAD vẽ lồng nhiều vòng tròn cho 1 cọc.</summary>
    public static List<PileSizeGroup> GroupCircles(IEnumerable<RawCircle> circles, IReadOnlyList<FamilySymbol> pileTypes)
    {
        var byKey = new Dictionary<long, (double DiameterMm, List<RawCircle> Circles)>();

        foreach (var circle in DeduplicateConcentric(circles, pileTypes))
        {
            var radiusMm = UnitUtils.ConvertFromInternalUnits(circle.RadiusFeet, UnitTypeId.Millimeters);
            var roundedRadiusMm = Math.Round(radiusMm / SnapUnitMm, MidpointRounding.AwayFromZero) * SnapUnitMm;
            var diameterMm = roundedRadiusMm * 2;
            var key = (long)Math.Round(roundedRadiusMm * 100);

            if (!byKey.TryGetValue(key, out var entry))
            {
                entry = (diameterMm, new List<RawCircle>());
                byKey[key] = entry;
            }

            entry.Circles.Add(circle);
            byKey[key] = entry;
        }

        return byKey.Values
            .OrderBy(e => e.DiameterMm)
            .Select(e => new PileSizeGroup { DiameterMm = e.DiameterMm, Circles = e.Circles })
            .ToList();
    }

    /// <summary>Gộp các circle đồng tâm (CAD hay vẽ lồng 2 vòng tròn cho 1 tim cọc) về 1 circle đại diện.</summary>
    private static List<RawCircle> DeduplicateConcentric(IEnumerable<RawCircle> circles, IReadOnlyList<FamilySymbol> pileTypes)
    {
        var tolFeet = UnitUtils.ConvertToInternalUnits(ConcentricTolMm, UnitTypeId.Millimeters);
        var clusters = new List<List<RawCircle>>();

        foreach (var circle in circles)
        {
            var cluster = clusters.FirstOrDefault(c => c[0].Center.DistanceTo(circle.Center) <= tolFeet);
            if (cluster is null)
                clusters.Add(new List<RawCircle> { circle });
            else
                cluster.Add(circle);
        }

        return clusters.Select(c => c.Count == 1 ? c[0] : PickRepresentativeCircle(c, pileTypes)).ToList();
    }

    /// <summary>Gộp các đường bao đồng tâm (CAD hay vẽ lồng 2 đường bao cho 1 đài) về 1 đường bao đại diện —
    /// so kích thước với box lớn nhất trong cụm: chênh lệch trong ngưỡng BoundaryDuplicateTolMm thì lấy box
    /// NHỎ HƠN (coi là cùng 1 đài vẽ trùng); chênh lệch vượt ngưỡng (khác biệt quá lớn, không phải trùng lặp)
    /// thì lấy lại box LỚN NHẤT.</summary>
    public static List<RawBoundary> DeduplicateOverlappingBoundaries(IEnumerable<RawBoundary> boundaries)
    {
        var tolFeet = UnitUtils.ConvertToInternalUnits(BoundaryDuplicateTolMm, UnitTypeId.Millimeters);
        var clusters = new List<List<RawBoundary>>();

        foreach (var boundary in boundaries)
        {
            var cluster = clusters.FirstOrDefault(c => c[0].Center.DistanceTo(boundary.Center) <= tolFeet);
            if (cluster is null)
                clusters.Add(new List<RawBoundary> { boundary });
            else
                cluster.Add(boundary);
        }

        return clusters.Select(c => PickRepresentativeBoundary(c, tolFeet)).ToList();
    }

    /// <summary>Đài móng từ đường bao đã chọn: gộp đường bao vẽ trùng, đánh số, rồi gán từng cọc vào đài chứa nó
    /// (point-in-polygon) — phải chạy TRƯỚC khi gộp nhóm cọc vì cao độ cọc tính theo đài.</summary>
    public static List<BoundaryRow> BuildCapRows(IEnumerable<RawBoundary> boundaries, IEnumerable<RawCircle> circles)
    {
        var deduped = DeduplicateOverlappingBoundaries(boundaries);
        var rows = deduped.Select((boundary, i) => new BoundaryRow { Index = i + 1, Boundary = boundary }).ToList();
        PileOwnershipResolver.AssignOwners(circles, rows);
        return rows;
    }

    /// <summary>1 cụm đường bao cùng vị trí: box nhỏ hơn chênh lệch kích thước trong ngưỡng thì lấy box đó, vượt ngưỡng thì lấy box lớn nhất.</summary>
    private static RawBoundary PickRepresentativeBoundary(List<RawBoundary> cluster, double tolFeet)
    {
        if (cluster.Count == 1)
            return cluster[0];

        var largest = cluster.OrderByDescending(b => Math.Max(b.LengthFeet, b.WidthFeet)).First();
        var smallest = cluster.OrderBy(b => Math.Max(b.LengthFeet, b.WidthFeet)).First();
        var sizeDiffFeet = Math.Max(largest.LengthFeet, largest.WidthFeet) - Math.Max(smallest.LengthFeet, smallest.WidthFeet);

        return sizeDiffFeet <= tolFeet ? smallest : largest;
    }

    /// <summary>Trong 1 cụm circle đồng tâm, ưu tiên circle có đường kính khớp sẵn 1 Type — không khớp thì lấy circle lớn nhất.</summary>
    private static RawCircle PickRepresentativeCircle(List<RawCircle> cluster, IReadOnlyList<FamilySymbol> pileTypes)
    {
        RawCircle? bestMatch = null;
        var bestDiff = double.MaxValue;

        foreach (var circle in cluster)
        {
            var diameterMm = UnitUtils.ConvertFromInternalUnits(circle.RadiusFeet * 2, UnitTypeId.Millimeters);
            var match = PileTypeMatcher.AutoMatch(diameterMm, pileTypes, s => s);
            var symbolDiameter = match is null ? null : PileTypeMatcher.DiameterMm(match);
            if (symbolDiameter is null)
                continue;

            var diff = Math.Abs(symbolDiameter.Value - diameterMm);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                bestMatch = circle;
            }
        }

        return bestMatch ?? cluster.OrderByDescending(c => c.RadiusFeet).First();
    }

    /// <summary>Chiều dày Type đài (Foundation Slab) — null nếu chưa chọn Type hoặc Type không có cấu tạo lớp.</summary>
    public static double? CapThicknessFeet(FloorType? capFloorType)
    {
        if (capFloorType is null)
            return null;
        try
        {
            return capFloorType.GetCompoundStructure()?.GetWidth();
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ Xem trước

    /// <summary>Xem trước bước Đặt, không sửa model. Đi đúng thứ tự kiểm tra của Place: trùng → không có đài chứa →
    /// thiếu chiều dày đài. Đài: thiếu Type/Level → trùng → không dựng được biên dạng.</summary>
    public static PilePlacementPreview PlanPlacement(
        Document doc, IReadOnlyList<PileAssignment> pileAssignments, IReadOnlyList<BoundaryRow> boundaryRows,
        Level? capLevel, FloorType? capFloorType, double? gridThetaDeg)
    {
        var existingBoxesByFamily = new Dictionary<string, List<(PlanBox Box, ElementId Id)>>(StringComparer.Ordinal);
        var thickness = CapThicknessFeet(capFloorType);
        var groups = new List<(PileSizeGroup Group, int ToCreate, int Duplicates, int NoCap, string? Problem)>();

        foreach (var (group, symbol) in pileAssignments)
        {
            if (symbol is null || capLevel is null)
            {
                groups.Add((group, 0, 0, 0, "chưa gán đủ Type cọc/Level đài"));
                continue;
            }

            if (!existingBoxesByFamily.TryGetValue(symbol.Family.Name, out var boxes))
            {
                boxes = BuildExistingBoxes(ExistingInstancesOf(doc, symbol.Family.Name), DuplicateExpandMm);
                existingBoxesByFamily[symbol.Family.Name] = boxes;
            }

            int toCreate = 0, duplicates = 0, noCap = 0;
            foreach (var circle in group.Circles)
            {
                if (FindDuplicate(circle.Center, boxes) is not null) duplicates++;
                else if (circle.OwningBoundary is null) noCap++;
                else toCreate++;
            }
            groups.Add((group, thickness is null ? 0 : toCreate, duplicates, noCap,
                thickness is null && toCreate > 0 ? "Type đài chưa chọn hoặc không có cấu tạo lớp, chưa tính được cao độ cọc" : null));
        }

        int capsToCreate = 0, capsDuplicates = 0, capsFailed = 0;
        if (capFloorType is null || capLevel is null)
        {
            capsFailed = boundaryRows.Count;
        }
        else
        {
            var capBoxes = BuildExistingBoxes(ExistingCapsOf(doc, capFloorType), DuplicateExpandMm);
            foreach (var row in boundaryRows)
            {
                if (FindDuplicate(row.Boundary.Center, capBoxes) is not null) capsDuplicates++;
                else if (PileBoundaryRegularizer.Regularize(row.Boundary, gridThetaDeg, capLevel.Elevation).Loop is null) capsFailed++;
                else capsToCreate++;
            }
        }

        return new PilePlacementPreview(groups, capsToCreate, capsDuplicates, capsFailed);
    }

    // ------------------------------------------------------------------ Đặt

    /// <summary>Đặt toàn bộ cọc (FamilyInstance) + đài (Foundation Slab) vào model trong 1 Transaction, bỏ qua vị trí đã có sẵn (chống trùng). Cao độ đỉnh cọc tính theo chiều dày Type đài đã chọn.</summary>
    public static PilePlaceSummary Place(
        Document doc, IReadOnlyList<PileAssignment> pileAssignments, IReadOnlyList<BoundaryRow> boundaryRows,
        Level? capLevel, FloorType? capFloorType, double embedmentMm, double? gridThetaDeg)
    {
        var embedmentFeet = UnitUtils.ConvertToInternalUnits(embedmentMm, UnitTypeId.Millimeters);
        var pilesCreated = 0;
        var pilesFailed = 0;
        var pilesSkipped = 0;
        var capsCreated = 0;
        var capsFailed = 0;
        var capsSkipped = 0;
        var errors = new List<string>();
        var createdPileIds = new List<ElementId>();
        var createdCapIds = new List<ElementId>();

        // Cache bounding box instance cu theo Family, dung 1 lan truoc vong foreach ben duoi (chi ap dung cho coc).
        var existingBoxesByFamily = new Dictionary<string, List<(PlanBox Box, ElementId Id)>>(StringComparer.Ordinal);

        List<(PlanBox Box, ElementId Id)> BoxesFor(string familyName)
        {
            if (!existingBoxesByFamily.TryGetValue(familyName, out var boxes))
            {
                boxes = BuildExistingBoxes(ExistingInstancesOf(doc, familyName), DuplicateExpandMm);
                existingBoxesByFamily[familyName] = boxes;
            }

            return boxes;
        }

        // Doc chieu day dai 1 LAN duy nhat cho ca lot Dat - Type dai dung chung cho moi dai.
        var foundationThicknessFeet = CapThicknessFeet(capFloorType);

        using var tx = new Transaction(doc, "Đặt Cọc + Đài móng theo CAD");
        tx.Start();

        foreach (var (group, symbol) in pileAssignments)
        {
            if (symbol is null || capLevel is null)
            {
                pilesFailed += group.Circles.Count;
                if (errors.Count < MaxErrors)
                    errors.Add($"Cọc Ø{group.DiameterMm:0}mm: chưa gán đủ Type cọc/Level đài, bỏ qua toàn bộ nhóm.");
                continue;
            }

            // So trung theo Family.Name, khong phan biet Type/duong kinh.
            var existingBoxes = BoxesFor(symbol.Family.Name);

            foreach (var circle in group.Circles)
            {
                if (FindDuplicate(circle.Center, existingBoxes) is not null)
                {
                    pilesSkipped++;
                    continue;
                }

                if (circle.OwningBoundary is null)
                {
                    pilesFailed++;
                    if (errors.Count < MaxErrors)
                        errors.Add($"Cọc Ø{group.DiameterMm:0}mm: không xác định được đài chứa (không có đài nào trong phạm vi), chưa đặt được cao độ.");
                    continue;
                }

                if (foundationThicknessFeet is null)
                {
                    pilesFailed++;
                    if (errors.Count < MaxErrors)
                        errors.Add($"Cọc Ø{group.DiameterMm:0}mm: chưa chọn Type đài (Foundation Slab) hoặc Type đó không có cấu tạo lớp (compound structure), chưa tính được cao độ.");
                    continue;
                }

                var pileTopElevFeet = capLevel.Elevation - foundationThicknessFeet.Value;

                var result = PlaceOne(doc, circle.Center, symbol, capLevel, pileTopElevFeet, embedmentFeet);
                if (result.Outcome == PlaceOutcome.Created)
                {
                    pilesCreated++;
                    if (result.CreatedId is not null)
                        createdPileIds.Add(result.CreatedId);
                }
                else
                {
                    pilesFailed++;
                    if (result.ErrorMessage is not null && errors.Count < MaxErrors)
                        errors.Add($"Cọc Ø{group.DiameterMm:0}mm: {result.ErrorMessage}");
                }
            }
        }

        // So trung dai theo Floor + dung FloorType.Id - dai la Floor, khong phai FamilyInstance.
        var capExistingBoxes = capFloorType is not null
            ? BuildExistingBoxes(ExistingCapsOf(doc, capFloorType), DuplicateExpandMm)
            : new List<(PlanBox Box, ElementId Id)>();

        var resolvedCapIds = new List<ElementId?>();
        var createdCapProfiles = new Dictionary<ElementId, CurveLoop>();

        foreach (var row in boundaryRows)
        {
            if (capFloorType is null || capLevel is null)
            {
                capsFailed++;
                resolvedCapIds.Add(null);
                if (errors.Count < MaxErrors)
                    errors.Add($"{row.Label}: chưa chọn Type đài (Foundation Slab) hoặc Level, bỏ qua.");
                continue;
            }

            var duplicateId = FindDuplicate(row.Boundary.Center, capExistingBoxes);
            if (duplicateId is not null)
            {
                capsSkipped++;
                resolvedCapIds.Add(duplicateId);
                continue;
            }

            var regularized = PileBoundaryRegularizer.Regularize(row.Boundary, gridThetaDeg, capLevel.Elevation);
            if (regularized.FallbackReason is not null && errors.Count < MaxErrors)
                errors.Add($"{row.Label}: {regularized.FallbackReason}");

            if (regularized.Loop is null)
            {
                capsFailed++;
                resolvedCapIds.Add(null);
                if (errors.Count < MaxErrors)
                    errors.Add($"{row.Label}: không dựng được biên dạng, bỏ qua.");
                continue;
            }

            try
            {
                var floor = Floor.Create(doc, new List<CurveLoop> { regularized.Loop }, capFloorType.Id, capLevel.Id, true, null, 0.0);
                capsCreated++;
                resolvedCapIds.Add(floor.Id);
                createdCapIds.Add(floor.Id);
                // Luu lai dung CurveLoop da dung de dung Floor - lop lot doc lai loop nay thay vi doc qua
                // Sketch.Profile (tranh sai so lam tron/mat do chinh xac khi round-trip qua Revit).
                createdCapProfiles[floor.Id] = regularized.Loop;
            }
            catch (Exception ex)
            {
                capsFailed++;
                resolvedCapIds.Add(null);
                if (errors.Count < MaxErrors)
                    errors.Add($"{row.Label}: {ex.Message}");
            }
        }

        tx.Commit();

        return new PilePlaceSummary(
            pilesCreated, pilesFailed, pilesSkipped, capsCreated, capsFailed, capsSkipped, errors,
            createdPileIds, createdCapIds, resolvedCapIds, createdCapProfiles);
    }

    private static IEnumerable<Element> ExistingInstancesOf(Document doc, string familyName) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .Where(fi => string.Equals(fi.Symbol?.Family?.Name, familyName, StringComparison.Ordinal));

    private static IEnumerable<Element> ExistingCapsOf(Document doc, FloorType capFloorType) =>
        new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_StructuralFoundation)
            .OfClass(typeof(Floor))
            .Cast<Floor>()
            .Where(f => f.FloorType.Id == capFloorType.Id);

    /// <summary>Bounding box (X/Y, đã mở rộng expandMm) của các element cho trước — dùng chung cho mọi cơ chế chống trùng (cọc/đài/lớp lót).</summary>
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

            boxes.Add((
                new PlanBox(bb.Min.X - expandFeet, bb.Max.X + expandFeet, bb.Min.Y - expandFeet, bb.Max.Y + expandFeet),
                element.Id));
        }

        return boxes;
    }

    /// <summary>Tìm ElementId của box đã có chứa điểm cho trước — null nếu không trùng box nào.</summary>
    internal static ElementId? FindDuplicate(XYZ point, List<(PlanBox Box, ElementId Id)> boxes)
    {
        foreach (var (box, id) in boxes)
        {
            if (box.Contains(point))
                return id;
        }

        return null;
    }

    /// <summary>Đặt 1 Family Instance cọc tại targetZ tuỳ ý (không nhất thiết = level.Elevation — level chỉ là Level tham chiếu truyền vào constructor), offset = 0 (gốc chèn family CHƯA xác nhận). embedmentFeet set thẳng vào biến Minimum Embedment của cọc (nếu family có) thay vì cộng vào targetZ.</summary>
    private static PlaceResult PlaceOne(Document doc, XYZ centerXY, FamilySymbol symbol, Level level, double targetZ, double embedmentFeet)
    {
        try
        {
            if (!symbol.IsActive)
                symbol.Activate();

            var point = new XYZ(centerXY.X, centerXY.Y, targetZ);

            FamilyInstance instance;
            try
            {
                instance = doc.Create.NewFamilyInstance(point, symbol, level, StructuralType.Footing);
            }
            catch (Exception)
            {
                instance = doc.Create.NewFamilyInstance(point, symbol, StructuralType.NonStructural);
            }

            instance.LookupParameter(PileEmbedmentParam)?.Set(embedmentFeet);

            FixElevation(doc, instance, targetZ);

            return new PlaceResult(PlaceOutcome.Created, CreatedId: instance.Id);
        }
        catch (Exception ex)
        {
            return new PlaceResult(PlaceOutcome.Failed, ex.Message);
        }
    }

    /// <summary>Đo lại Z thực tế sau khi đặt và bù chênh lệch về đúng targetZ — tránh trường hợp overload NewFamilyInstance có Level cộng dồn thêm Level.Elevation vào Z đã truyền vào.</summary>
    private static void FixElevation(Document doc, FamilyInstance instance, double targetZ)
    {
        doc.Regenerate();

        if (instance.Location is not LocationPoint lp)
            return;

        var deltaZ = targetZ - lp.Point.Z;
        if (Math.Abs(deltaZ) > 1e-6)
            ElementTransformUtils.MoveElement(doc, instance.Id, new XYZ(0, 0, deltaZ));
    }

    // ------------------------------------------------------------------ Tạo Type còn thiếu

    /// <summary>Xem trước bước tạo Type: tên Type mới theo đường kính, hoặc lỗi (không có Type mẫu / tên đã tồn tại).</summary>
    public static List<(PileSizeGroup Group, string? NewTypeName, string? Error)> PlanMissingTypes(
        Document doc, IReadOnlyList<(PileSizeGroup Group, FamilySymbol? Template)> groups)
    {
        var previews = new List<(PileSizeGroup, string?, string?)>();
        var plannedByFamily = new Dictionary<ElementId, HashSet<string>>();
        foreach (var (group, template) in groups)
        {
            if (template is null)
            {
                previews.Add((group, null, "chưa có Type cọc nào có tham số Diameter để nhân bản"));
                continue;
            }

            var newName = $"{(long)Math.Round(group.DiameterMm)}";
            if (!plannedByFamily.TryGetValue(template.Family.Id, out var names))
                plannedByFamily[template.Family.Id] = names = ExistingNames(doc, template.Family);
            if (!names.Add(newName))
            {
                previews.Add((group, null, $"tên Type \"{newName}\" đã tồn tại trong family \"{template.Family.Name}\" — không ghi đè"));
                continue;
            }
            previews.Add((group, newName, null));
        }
        return previews;
    }

    /// <summary>Tự tạo Type cọc còn thiếu — mỗi nhóm duplicate Type mẫu cho trước (thường là Type gần đúng nhất), đổi tên
    /// theo đường kính thật + set lại tham số Diameter. Tên đã tồn tại thì bỏ qua, KHÔNG ghi đè.</summary>
    public static PileCreateTypesSummary CreateMissingTypes(Document doc, IReadOnlyList<(PileSizeGroup Group, FamilySymbol? Template)> groups)
    {
        var created = 0;
        var failed = 0;
        var errors = new List<string>();
        var createdSymbols = new List<FamilySymbol>();
        var resolved = new Dictionary<PileSizeGroup, FamilySymbol>();

        using var tx = new Transaction(doc, "Tạo Type Cọc còn thiếu");
        tx.Start();

        foreach (var (group, template) in groups)
        {
            if (template is null)
            {
                failed++;
                errors.Add($"Cọc {group.DiameterLabel}: chưa có Type gần đúng nào để nhân bản — chọn tay 1 Type trong ComboBox trước.");
                continue;
            }

            var newName = $"{(long)Math.Round(group.DiameterMm)}";
            var family = template.Family;
            if (ExistingNames(doc, family).Contains(newName))
            {
                failed++;
                errors.Add($"Cọc {group.DiameterLabel}: tên Type \"{newName}\" đã tồn tại trong family \"{family.Name}\" — bỏ qua, KHÔNG ghi đè.");
                continue;
            }

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
                errors.Add($"Cọc {group.DiameterLabel}: lỗi khi tạo Type — {ex.Message}");
                continue;
            }

            var diameterParam = newSymbol.LookupParameter(PileTypeMatcher.PileDiameterParam);
            if (diameterParam is null || diameterParam.StorageType != StorageType.Double)
            {
                failed++;
                errors.Add($"Family \"{family.Name}\" không có tham số \"{PileTypeMatcher.PileDiameterParam}\" dạng Length — Type \"{newName}\" đã tạo nhưng KHÔNG đúng kích thước, kiểm tra lại family.");
                continue;
            }

            diameterParam.Set(UnitUtils.ConvertToInternalUnits(group.DiameterMm, UnitTypeId.Millimeters));

            created++;
            createdSymbols.Add(newSymbol);
            resolved[group] = newSymbol;
        }

        tx.Commit();
        return new PileCreateTypesSummary(created, failed, errors, createdSymbols, resolved);
    }

    private static HashSet<string> ExistingNames(Document doc, Family family) =>
        family.GetFamilySymbolIds()
            .Select(id => doc.GetElement(id)?.Name)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Xem trước bước Đặt cọc + đài. NoCap: cọc không nằm trong đài nào nên không tính được cao độ.</summary>
internal sealed record PilePlacementPreview(
    IReadOnlyList<(PileSizeGroup Group, int ToCreate, int Duplicates, int NoCap, string? Problem)> Groups,
    int CapsToCreate, int CapsDuplicates, int CapsFailed);
