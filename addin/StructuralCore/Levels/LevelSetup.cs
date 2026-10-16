using System.IO;
using Autodesk.Revit.DB;
using View = Autodesk.Revit.DB.View;

namespace Bimtech.StructuralCore.Levels;

internal enum CadPlacementMode
{
    Import,
    Link
}

/// <summary>1 tầng cần dựng: file DWG mặt bằng kết cấu, tên + cao độ Level, Import hay Link, có dựng Grid từ CAD này không.
/// FilePath trống: chỉ tạo Level + Structural Plan, không nhập CAD.</summary>
internal sealed record LevelSetupRow(string FilePath, string LevelName, double ElevationMm, CadPlacementMode Mode, bool CreateGrids)
{
    public bool HasCad => !string.IsNullOrWhiteSpace(FilePath);

    /// <summary>Đơn vị đọc DWG. Mặc định mm như tool WPF; Default = theo đơn vị ghi trong file.</summary>
    public ImportUnit Unit { get; init; } = ImportUnit.Millimeter;

    /// <summary>View template và tỉ lệ cho Structural Plan mới tạo (null: giữ mặc định của Revit).</summary>
    public ElementId? ViewTemplateId { get; init; }
    public int? Scale { get; init; }
}

/// <summary>Level không ai dùng: không có phần tử nào phụ thuộc ngoài các view của chính nó, và không view nào nằm trên sheet.
/// Views: số view sẽ bị xoá kèm.</summary>
internal sealed record UnusedLevel(Level Level, int Views);

/// <summary>Xem trước 1 tầng, không sửa model. Problem khác null nghĩa là ApplyRow sẽ báo lỗi cho tầng này.
/// ExistingLevel: Level cùng tên. RenameFrom: không có Level cùng tên nhưng có đúng 1 Level khác tên ở cùng cao độ (thường
/// là "Level 1" của template) — sẽ đổi tên Level đó thay vì tạo Level trùng cao độ. PlanName: tên plan sẽ dùng (plan có sẵn
/// hoặc tên mới không trùng view nào); RenamesPlan: plan có sẵn đang mang tên cũ của Level (tên Revit tự đặt) sẽ đổi theo.</summary>
internal sealed record LevelRowPreview(
    LevelSetupRow Row, Level? ExistingLevel, Level? RenameFrom, ViewPlan? ExistingPlan, string PlanName, bool RenamesPlan,
    string? Problem)
{
    public bool CreatesLevel => ExistingLevel is null && RenameFrom is null;
    public bool CreatesPlan => ExistingPlan is null;
}

/// <summary>Tạo Level + Structural Plan "KC - tên" cho từng tầng, nhập CAD vào đúng view đó, tuỳ chọn dựng Grid từ CAD
/// và căn mọi CAD theo cùng hệ Grid. Preview chỉ đọc; ApplyRow mở Transaction riêng cho mỗi tầng như trước khi tách.</summary>
internal static class LevelSetup
{
    private static readonly double ElevationToleranceFeet = UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Millimeters);

    /// <summary>Ký tự Revit không cho dùng trong tên Level và tên view.</summary>
    public const string ForbiddenNameChars = "\\:{}[]|;<>?`~";

    internal sealed record RowResult(int GridCount, XYZ? AlignmentTranslation, ElementId LevelId, ElementId PlanId, ElementId? CadId);

    public static ViewFamilyType? FindStructuralPlanType(Document doc) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
            .FirstOrDefault(x => x.ViewFamily == ViewFamily.StructuralPlan);

    /// <summary>Thứ tự chạy: tầng có dựng Grid trước, vì vector căn chỉnh của nó dùng chung cho mọi tầng còn lại.</summary>
    public static List<LevelSetupRow> OrderForApply(IEnumerable<LevelSetupRow> rows) =>
        rows.OrderByDescending(r => r.CreateGrids).ToList();

    public static string PlanNameFor(string levelName) => $"KC - {levelName.Trim()}";

    /// <summary>Kiểm tra trước từng tầng giống các điều kiện ApplyRow sẽ kiểm tra, không sửa model.</summary>
    public static List<LevelRowPreview> Preview(Document doc, IReadOnlyList<LevelSetupRow> rows)
    {
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
        var plans = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().ToList();
        var viewNames = ViewNames(doc);
        var requestedNames = rows.Select(r => r.LevelName.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gridRows = rows.Count(r => r.CreateGrids);
        var previews = new List<LevelRowPreview>();

        foreach (var row in rows)
        {
            var name = row.LevelName.Trim();
            var elevation = ToFeet(row.ElevationMm);
            var level = FindLevel(levels, name);
            // Không có Level cùng tên: Level khác tên ở cùng cao độ (và không bị dòng khác giành theo tên) được đổi tên.
            List<Level> sameElevation = level is not null ? [] : levels
                .Where(l => Math.Abs(l.Elevation - elevation) <= ElevationToleranceFeet && !requestedNames.Contains(l.Name))
                .ToList();
            var renameFrom = sameElevation.Count == 1 ? sameElevation[0] : null;
            var target = level ?? renameFrom;
            var plan = target is null ? null : FindStructuralPlan(plans, target);
            // Plan mang đúng tên cũ của Level là tên Revit tự đặt: đổi theo tên mới cho khớp.
            var renamesPlan = renameFrom is not null && plan is not null
                && string.Equals(plan.Name, renameFrom.Name, StringComparison.OrdinalIgnoreCase);
            var planName = plan is not null && !renamesPlan ? plan.Name : UniqueName(PlanNameFor(name), viewNames);
            string? problem = null;

            if (string.IsNullOrWhiteSpace(name))
                problem = "Tên Level trống.";
            else if (ForbiddenIn(name) is { } forbidden)
                problem = $"Tên Level có ký tự Revit không cho dùng: {forbidden}. Các ký tự cấm: {ForbiddenNameChars}";
            else if (sameElevation.Count > 1)
                problem = $"Đã có {sameElevation.Count} Level ở cùng cao độ ({string.Join(", ", sameElevation.Select(l => l.Name))}); "
                    + "đặt tên tầng trùng với một trong số đó để dùng lại.";
            else if (rows.Count(r => Math.Abs(ToFeet(r.ElevationMm) - elevation) <= ElevationToleranceFeet) > 1)
                problem = $"Có nhiều tầng trong danh sách cùng cao độ {row.ElevationMm:0.##} mm.";
            else if (row.HasCad && !File.Exists(row.FilePath))
                problem = "Không tìm thấy file DWG.";
            else if (!row.HasCad && row.CreateGrids)
                problem = "Cần file DWG để dựng Grid.";
            else if (level is not null && Math.Abs(level.Elevation - ToFeet(row.ElevationMm)) > ElevationToleranceFeet)
                problem = $"Level đã có nhưng cao độ khác {row.ElevationMm:0.##} mm.";
            else if (rows.Count(r => string.Equals(r.LevelName.Trim(), name, StringComparison.OrdinalIgnoreCase)) > 1)
                problem = "Tên Level bị lặp trong danh sách.";
            else if (row.CreateGrids && gridRows > 1)
                problem = "Chỉ dựng Grid được từ 1 tầng: tầng dựng sau sẽ báo trùng tên Grid. Các tầng khác tự căn theo tầng đó.";

            if (problem is null && (plan is null || renamesPlan)) viewNames.Add(planName);
            previews.Add(new LevelRowPreview(row, level, renameFrom, plan, planName, renamesPlan, problem));
        }

        return previews;
    }

    /// <summary>Dựng 1 tầng trong 1 Transaction riêng. Ném InvalidOperationException khi lỗi — Transaction tự rollback,
    /// các tầng khác không bị ảnh hưởng. <paramref name="existingAlignment"/>: vector căn chỉnh của tầng dựng Grid
    /// (null nếu chưa có) — tầng không dựng Grid được dời theo vector này để chung hệ Grid.</summary>
    public static RowResult ApplyRow(Document doc, ViewFamilyType structuralType, LevelSetupRow row, XYZ? existingAlignment)
    {
        if (row.HasCad && !File.Exists(row.FilePath)) throw new InvalidOperationException("Không tìm thấy file DWG.");

        using var tx = new Transaction(doc, $"CAD tầng {row.LevelName}");
        tx.Start();
        var result = ApplyRowInTransaction(doc, structuralType, row, existingAlignment);
        tx.Commit();
        return result;
    }

    /// <summary>Như ApplyRow nhưng chạy trong Transaction do caller mở sẵn (khung plan của MCP: mỗi mục 1 Transaction).
    /// <paramref name="renameLevelId"/>: Level khác tên ở cùng cao độ mà Preview đã chọn để đổi tên (RenameFrom), thay vì
    /// tạo Level trùng cao độ. Ném InvalidOperationException khi lỗi; caller rollback.</summary>
    public static RowResult ApplyRowInTransaction(
        Document doc, ViewFamilyType structuralType, LevelSetupRow row, XYZ? existingAlignment, ElementId? renameLevelId = null)
    {
        if (row.HasCad && !File.Exists(row.FilePath)) throw new InvalidOperationException("Không tìm thấy file DWG.");
        if (!row.HasCad && row.CreateGrids) throw new InvalidOperationException("Cần file DWG để dựng Grid.");

        var name = row.LevelName.Trim();
        if (ForbiddenIn(name) is { } forbidden)
            throw new InvalidOperationException($"Tên Level có ký tự Revit không cho dùng: {forbidden}.");
        var elevation = ToFeet(row.ElevationMm);
        var level = FindLevel(new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>(), name);
        string? oldLevelName = null;

        if (level == null && renameLevelId is not null)
        {
            level = doc.GetElement(renameLevelId) as Level
                ?? throw new InvalidOperationException("Level cần đổi tên không còn trong model.");
            if (Math.Abs(level.Elevation - elevation) > ElevationToleranceFeet)
                throw new InvalidOperationException($"Level \"{level.Name}\" không còn ở cao độ {row.ElevationMm:0.##} mm.");
            oldLevelName = level.Name;
            level.Name = name;
        }
        else if (level == null)
        {
            level = Level.Create(doc, elevation);
            level.Name = name;
        }
        else if (Math.Abs(level.Elevation - elevation) > ElevationToleranceFeet)
        {
            throw new InvalidOperationException($"Level đã có nhưng cao độ khác {row.ElevationMm:0.##} mm.");
        }

        // Tên plan luôn là tên Preview đã báo: "KC - tên", thêm " (2)", " (3)"... khi một view khác đã dùng tên đó.
        var plan = FindStructuralPlan(new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>(), level);
        if (plan == null)
        {
            plan = ViewPlan.Create(doc, structuralType.Id, level.Id);
            plan.Name = UniqueName(PlanNameFor(name), ViewNames(doc, except: plan.Id));
            // Tỉ lệ trước, template sau: template quản lý tỉ lệ thì nó thắng, thay vì Revit báo tỉ lệ chỉ đọc.
            if (row.Scale is { } scale) plan.Scale = scale;
            if (row.ViewTemplateId is { } template) plan.ViewTemplateId = template;
        }
        else if (oldLevelName is not null && string.Equals(plan.Name, oldLevelName, StringComparison.OrdinalIgnoreCase))
        {
            plan.Name = UniqueName(PlanNameFor(name), ViewNames(doc, except: plan.Id));
        }

        if (!row.HasCad) return new RowResult(0, null, level.Id, plan.Id, null);

        var options = new DWGImportOptions
        {
            Placement = ImportPlacement.Origin,
            ThisViewOnly = true,
            ColorMode = ImportColorMode.Preserved,
            Unit = row.Unit,
            VisibleLayersOnly = false,
            OrientToView = true,
            AutoCorrectAlmostVHLines = true
        };

        ElementId cadId;
        var ok = row.Mode == CadPlacementMode.Import
            ? doc.Import(row.FilePath, options, plan, out cadId)
            : doc.Link(row.FilePath, options, plan, out cadId);
        if (!ok || cadId == ElementId.InvalidElementId)
            throw new InvalidOperationException($"Không thể {row.Mode} file CAD vào Structural Plan.");

        var gridCount = 0;
        XYZ? newAlignment = null;
        if (row.CreateGrids)
        {
            doc.Regenerate();
            if (doc.GetElement(cadId) is not ImportInstance cadInstance)
                throw new InvalidOperationException("Không đọc được đối tượng CAD vừa nhập để tạo Grid.");
            var buildResult = GridBuilder.Create(doc, plan, cadInstance);
            gridCount = buildResult.GridCount;
            newAlignment = buildResult.AlignmentTranslation;
        }
        else if (existingAlignment != null)
        {
            if (doc.GetElement(cadId) is not ImportInstance cadInstance)
                throw new InvalidOperationException("Không đọc được đối tượng CAD vừa nhập để căn chỉnh.");
            GridBuilder.MoveCad(doc, cadInstance, existingAlignment);
        }

        return new RowResult(gridCount, newAlignment, level.Id, plan.Id, cadId);
    }

    /// <summary>Các Level không dùng tới, trừ <paramref name="keep"/> (Level được giữ hoặc sẽ đổi tên). Chỉ đọc model.</summary>
    public static List<UnusedLevel> FindUnused(Document doc, IReadOnlySet<ElementId> keep) =>
        new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
            .Where(level => !keep.Contains(level.Id))
            .Select(level => (Level: level, Blockers: DeletionBlockers(doc, level, out var views), Views: views))
            .Where(entry => entry.Blockers.Count == 0)
            .Select(entry => new UnusedLevel(entry.Level, entry.Views))
            .OrderBy(entry => entry.Level.Elevation)
            .ToList();

    /// <summary>Xoá 1 Level không dùng tới, trong Transaction do caller mở sẵn. Kiểm tra lại ngay trước khi xoá: nếu giờ đã
    /// có phần tử phụ thuộc, ném InvalidOperationException và không xoá gì.</summary>
    public static int DeleteUnusedInTransaction(Document doc, Level level)
    {
        var blockers = DeletionBlockers(doc, level, out _);
        if (blockers.Count > 0)
            throw new InvalidOperationException(
                $"Level \"{level.Name}\" giờ đang được dùng ({string.Join(", ", blockers.Take(5))}); không xoá.");
        return doc.Delete(level.Id).Count;
    }

    // What would be deleted with the level besides its own views (and the elements owned by those views), plus
    // views of it placed on a sheet. Empty means deleting the level loses nothing the person made.
    private static List<string> DeletionBlockers(Document doc, Level level, out int views)
    {
        var levelViews = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Where(view => !view.IsTemplate && view.GenLevel?.Id == level.Id)
            .Select(view => view.Id)
            .ToHashSet();
        views = levelViews.Count;
        var onSheets = new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>()
            .Where(viewport => levelViews.Contains(viewport.ViewId))
            .Select(viewport => $"view {doc.GetElement(viewport.ViewId)?.Name} trên sheet");
        // Revit cannot delete the view it is showing.
        if (doc.ActiveView is { } active && levelViews.Contains(active.Id))
            onSheets = onSheets.Append($"view {active.Name} đang mở");
        var dependents = level.GetDependentElements(null)
            .Where(id => id != level.Id && !levelViews.Contains(id))
            .Select(doc.GetElement)
            .Where(element => element is not null
                && element.Category is not null
                && element is not SketchPlane
                && !levelViews.Contains(element.OwnerViewId))
            .Select(element => $"{element!.Category!.Name} {element.Id}");
        return onSheets.Concat(dependents).ToList();
    }

    /// <summary>CAD đã nằm trong view này có cùng tên file (tên Type của ImportInstance) — nhập lại sẽ thành 2 bản.</summary>
    public static bool PlanHasCadFile(Document doc, ViewPlan plan, string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        return new FilteredElementCollector(doc, plan.Id).OfClass(typeof(ImportInstance)).Cast<ImportInstance>()
            .Any(cad => string.Equals(doc.GetElement(cad.GetTypeId())?.Name, fileName, StringComparison.OrdinalIgnoreCase));
    }

    private static double ToFeet(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);

    private static Level? FindLevel(IEnumerable<Level> levels, string name) =>
        levels.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

    private static ViewPlan? FindStructuralPlan(IEnumerable<ViewPlan> plans, Level level) =>
        plans.FirstOrDefault(v => v.GenLevel?.Id == level.Id && v.ViewType == ViewType.EngineeringPlan);

    private static HashSet<string> ViewNames(Document doc, ElementId? except = null) =>
        new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Where(v => v.Id != except)
            .Select(v => v.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // "KC - Tầng 2", else "KC - Tầng 2 (2)", "KC - Tầng 2 (3)"...
    private static string UniqueName(string name, ISet<string> taken)
    {
        var candidate = name;
        for (var suffix = 2; taken.Contains(candidate); suffix++) candidate = $"{name} ({suffix})";
        return candidate;
    }

    // The forbidden characters found in a name, e.g. "':', '?'"; null when the name is fine.
    private static string? ForbiddenIn(string name)
    {
        var found = name.Where(ForbiddenNameChars.Contains).Distinct().Select(c => $"'{c}'").ToList();
        return found.Count == 0 ? null : string.Join(", ", found);
    }
}
