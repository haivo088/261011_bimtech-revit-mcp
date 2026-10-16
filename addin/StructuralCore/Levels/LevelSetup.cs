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
}

/// <summary>Xem trước 1 tầng, không sửa model. Problem khác null nghĩa là ApplyRow sẽ báo lỗi cho tầng này.</summary>
internal sealed record LevelRowPreview(
    LevelSetupRow Row, Level? ExistingLevel, ViewPlan? ExistingPlan, string PlanName, string? Problem)
{
    public bool CreatesLevel => ExistingLevel is null;
    public bool CreatesPlan => ExistingPlan is null;
}

/// <summary>Tạo Level + Structural Plan "KC - tên" cho từng tầng, nhập CAD vào đúng view đó, tuỳ chọn dựng Grid từ CAD
/// và căn mọi CAD theo cùng hệ Grid. Preview chỉ đọc; ApplyRow mở Transaction riêng cho mỗi tầng như trước khi tách.</summary>
internal static class LevelSetup
{
    private static readonly double ElevationToleranceFeet = UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Millimeters);

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
        var gridRows = rows.Count(r => r.CreateGrids);
        var previews = new List<LevelRowPreview>();

        foreach (var row in rows)
        {
            var name = row.LevelName.Trim();
            var level = FindLevel(levels, name);
            var plan = level is null ? null : FindStructuralPlan(plans, level);
            string? problem = null;

            if (string.IsNullOrWhiteSpace(name))
                problem = "Tên Level trống.";
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

            previews.Add(new LevelRowPreview(row, level, plan, PlanNameFor(name), problem));
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
    /// Ném InvalidOperationException khi lỗi; caller rollback.</summary>
    public static RowResult ApplyRowInTransaction(Document doc, ViewFamilyType structuralType, LevelSetupRow row, XYZ? existingAlignment)
    {
        if (row.HasCad && !File.Exists(row.FilePath)) throw new InvalidOperationException("Không tìm thấy file DWG.");
        if (!row.HasCad && row.CreateGrids) throw new InvalidOperationException("Cần file DWG để dựng Grid.");

        var name = row.LevelName.Trim();
        var elevation = ToFeet(row.ElevationMm);
        var level = FindLevel(new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>(), name);

        if (level == null)
        {
            level = Level.Create(doc, elevation);
            level.Name = name;
        }
        else if (Math.Abs(level.Elevation - elevation) > ElevationToleranceFeet)
        {
            throw new InvalidOperationException($"Level đã có nhưng cao độ khác {row.ElevationMm:0.##} mm.");
        }

        var plan = FindStructuralPlan(new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>(), level);
        if (plan == null)
        {
            plan = ViewPlan.Create(doc, structuralType.Id, level.Id);
            var desiredName = PlanNameFor(name);
            if (!ViewNameExists(doc, desiredName, plan.Id)) plan.Name = desiredName;
        }

        if (!row.HasCad) return new RowResult(0, null, level.Id, plan.Id, null);

        var options = new DWGImportOptions
        {
            Placement = ImportPlacement.Origin,
            ThisViewOnly = true,
            ColorMode = ImportColorMode.Preserved,
            Unit = ImportUnit.Millimeter,
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

    private static bool ViewNameExists(Document doc, string name, ElementId exceptId) =>
        new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.View)).Cast<Autodesk.Revit.DB.View>()
            .Any(v => v.Id != exceptId && string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
}
