using System.Globalization;
using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands.Common;
using Bimtech.RevitMcp.Commands.Plans;
using Bimtech.RevitMcp.Planning;
using Bimtech.RevitMcp.Protocol;
using Bimtech.RevitMcp.Recognition;
using Bimtech.RevitMcp.Ui;
using Bimtech.StructuralCore.Levels;

namespace Bimtech.RevitMcp.Commands.Structural;

// Step 1 of setting up a structural model: one plan item per storey that creates the level (or renames a level of
// another name at that elevation), its structural plan "KC - <name>" and imports or links that storey's CAD at the
// origin. Levels nobody uses (template leftovers) get a delete item that starts rejected. Read-only: it checks every
// storey with LevelSetup.Preview and saves a plan, but never opens a Transaction.
// Grids come later from propose_grids, because they are read from CAD that must already be in the model.
public sealed class ProposeLevelsCommand(PlanService plans, ReviewWindowHost review, ModelChangeTracker changes) : IRevitCommand
{
    public string Method => "propose_levels";
    // Saves a plan file but never changes the model.
    public CommandAccess Access => CommandAccess.Read;

    private const int MaxLevels = 100;
    private const int MaxTemplatesListed = 30;

    private static readonly Dictionary<string, ImportUnit> CadUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mm"] = ImportUnit.Millimeter,
        ["cm"] = ImportUnit.Centimeter,
        ["m"] = ImportUnit.Meter,
        ["auto"] = ImportUnit.Default,
    };

    public object? Execute(CommandContext context)
    {
        var p = context.Params;
        var document = context.RequireDocument();
        if (document.IsFamilyDocument)
            throw new RpcException(RpcErrorCodes.NotWritable, "The active document is a family. Open a project model.");

        var input = p.GetObjectList("levels", MaxLevels);
        if (input.Count == 0) throw RpcException.InvalidParams("levels", "give at least one storey.");
        var template = p.GetString("viewTemplate", 255) is { } templateName ? RequirePlanTemplate(document, templateName) : null;
        int? scale = p.Has("scale") ? p.GetInt("scale", 100, 1, 10_000) : null;
        var rows = input.Select(ReadRow).Select(row => row with { ViewTemplateId = template?.Id, Scale = scale }).ToList();

        var hasPlanType = LevelSetup.FindStructuralPlanType(document) is not null;
        var basePointMm = UnitConvert.Length(BasePoint.GetProjectBasePoint(document).Position.Z);
        var previews = LevelSetup.Preview(document, rows);
        var heightWarnings = HeightWarnings(rows);

        var items = new List<PlanItem>();
        var unchanged = new List<string>();
        foreach (var (preview, index) in previews.Select((preview, index) => (preview, index)))
        {
            context.Cancellation.ThrowIfCancellationRequested();
            if (preview.Problem is null && preview.ExistingLevel is not null && !preview.CreatesPlan && !preview.Row.HasCad)
                unchanged.Add(preview.Row.LevelName.Trim());
            else
                items.Add(ToItem(document, preview, index, hasPlanType, basePointMm, heightWarnings.GetValueOrDefault(index)));
        }
        if (items.Count == 0)
            return new
            {
                plan = (object?)null,
                unchangedLevels = unchanged,
                note = "Every storey already has its level and structural plan, and no CAD file was given. No plan was created.",
            };

        // Levels kept or renamed by this plan are never offered for deletion.
        var keep = previews.SelectMany(preview => new[] { preview.ExistingLevel, preview.RenameFrom })
            .OfType<Level>().Select(level => level.Id).ToHashSet();
        var unused = p.GetBool("proposeLevelCleanup", true) ? LevelSetup.FindUnused(document, keep) : [];
        items.AddRange(unused.Select((level, index) => DeleteItem(level, index)));
        // Grids come later from propose_grids; when the model has them already, the new levels are fitted to them.
        var (gridMargin, levelMargin) = DatumExtentsItem.ReadMargins(p);
        var grids = new FilteredElementCollector(document).OfClass(typeof(Grid)).GetElementCount();
        var requested = rows.Select(row => row.LevelName.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var levelsAfter = new FilteredElementCollector(document).OfClass(typeof(Level)).Cast<Level>()
            .Where(level => !requested.Contains(level.Name) && !previews.Any(preview => preview.RenameFrom?.Id == level.Id))
            .Select(level => (level.Name, UnitConvert.Length(level.Elevation)))
            .Concat(previews.Where(preview => preview.Problem is null)
                .Select(preview => (preview.Row.LevelName.Trim(), preview.Row.ElevationMm)))
            .ToList();
        var fit = DatumExtentsItem.Create(levelsAfter, grids, gridMargin, levelMargin);
        if (fit is not null) items.Add(fit);

        var plan = plans.Create(
            PlanStorageLocation.For(document),
            kind: "create_levels",
            title: $"Tạo {items.Count - unused.Count - (fit is null ? 0 : 1)} tầng (Level, Structural Plan, CAD)",
            scope: JsonSerializer.SerializeToElement(new
            {
                levels = rows.Select(row => new { name = row.LevelName.Trim(), elevationMm = row.ElevationMm }),
                unchangedLevels = unchanged,
                viewTemplate = template?.Name,
                scale,
                projectBasePointElevationMm = basePointMm,
            }),
            items: items,
            modelStamp: changes.Current(document));
        review.Show(plan, context.UIApplication);

        return new
        {
            plan = PlanResults.Status(plan),
            unchangedLevels = unchanged,
            unusedLevelsOffered = unused.Select(level => level.Level.Name).ToList(),
            heightWarnings = heightWarnings.Values,
            projectBasePointElevationMm = basePointMm,
            reviewWindow = "opened",
            note = "Nothing was changed in the model. The review window is open in Revit; ask the user to review there "
                + "and say when they are done, then call get_plan_status and commit_plan. Items that delete unused levels "
                + "start rejected; the user approves them only to delete. After the commit, check each item's cadWarning "
                + "(wrong DWG unit), then call list_cad_sources and propose_grids to build grids and align every storey's CAD.",
        };
    }

    private static LevelSetupRow ReadRow(CommandParams level, int index)
    {
        var name = level.RequireString("name", 255).Trim();
        if (name.Length == 0) throw RpcException.InvalidParams($"levels[{index}].name", "must not be empty.");
        var cadFile = level.GetString("cadFile", 1024)?.Trim();
        if (!string.IsNullOrEmpty(cadFile) && !string.Equals(Path.GetExtension(cadFile), ".dwg", StringComparison.OrdinalIgnoreCase))
            throw RpcException.InvalidParams($"levels[{index}].cadFile", "must be a .dwg file.");
        var unitText = level.GetString("cadUnit", 8) ?? "mm";
        if (!CadUnits.TryGetValue(unitText, out var unit))
            throw RpcException.InvalidParams($"levels[{index}].cadUnit", $"must be one of {string.Join(", ", CadUnits.Keys)}.");
        return new LevelSetupRow(
            cadFile ?? "",
            name,
            level.RequireDouble("elevationMm", -100_000, 1_000_000),
            level.GetEnum("mode", CadPlacementMode.Import),
            CreateGrids: false)
        {
            Unit = unit,
        };
    }

    // A structural plan view template, by name. The error lists the ones the model has.
    private static View RequirePlanTemplate(Document document, string name)
    {
        var templates = new FilteredElementCollector(document).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
            .Where(view => view.IsTemplate && view.ViewType == ViewType.EngineeringPlan)
            .ToList();
        return templates.FirstOrDefault(view => string.Equals(view.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw RpcException.InvalidParams("viewTemplate",
                $"no structural plan view template named '{name}'. Available: "
                + (templates.Count == 0 ? "none" : string.Join(", ", templates.Take(MaxTemplatesListed).Select(view => view.Name))) + ".");
    }

    // Unusual storey height between consecutive requested storeys, keyed by the index of the upper one.
    private static Dictionary<int, string> HeightWarnings(IReadOnlyList<LevelSetupRow> rows)
    {
        var sorted = rows.Select((row, index) => (row, index)).OrderBy(entry => entry.row.ElevationMm).ToList();
        var warnings = new Dictionary<int, string>();
        foreach (var (lower, upper) in sorted.Zip(sorted.Skip(1)))
            if (StoreyNames.HeightWarnings([(lower.row.LevelName.Trim(), lower.row.ElevationMm), (upper.row.LevelName.Trim(), upper.row.ElevationMm)])
                    .FirstOrDefault() is { } warning)
                warnings[upper.index] = warning;
        return warnings;
    }

    // Problems become Blocked items with a reason instead of errors, so one bad storey does not hide the rest.
    private static PlanItem ToItem(
        Document document, LevelRowPreview preview, int index, bool hasPlanType, double basePointMm, string? heightWarning)
    {
        var row = preview.Row;
        var name = row.LevelName.Trim();
        var reasons = new List<string>();
        var problem = preview.Problem
            ?? (preview.CreatesPlan && !hasPlanType ? "Model chưa có loại view Structural Plan." : null);
        if (problem is not null) reasons.Add(problem);
        if (heightWarning is not null) reasons.Add(heightWarning);
        if (preview.ExistingLevel is not null) reasons.Add("Level đã có, dùng lại.");
        if (preview.RenameFrom is { } old)
            reasons.Add($"Level \"{old.Name}\" đã có ở cùng cao độ: đổi tên thay vì tạo Level trùng cao độ. Bỏ mục này nếu muốn giữ tên cũ.");
        if (preview.CreatesPlan && preview.PlanName != LevelSetup.PlanNameFor(name))
            reasons.Add($"Tên \"{LevelSetup.PlanNameFor(name)}\" đã có view khác dùng; plan mới tên \"{preview.PlanName}\".");
        if (row.HasCad && preview.ExistingPlan is { } plan && LevelSetup.PlanHasCadFile(document, plan, row.FilePath))
            reasons.Add("Plan này đã có CAD cùng tên file; nhập nữa sẽ thành 2 bản.");
        // Not yet checked in Revit which base Level.Create and Level.Elevation use when the base point is raised.
        if (preview.ExistingLevel is null && Math.Abs(basePointMm) >= 1)
            reasons.Add($"Project Base Point đang ở cao độ {basePointMm:0.##} mm; mốc cao độ trong trường hợp này chưa được "
                + "kiểm chứng: xem lại cao độ Level sau khi ghi.");

        var cadFile = row.HasCad ? row.FilePath : null;
        return new PlanItem
        {
            ItemId = "L" + (index + 1).ToString(CultureInfo.InvariantCulture),
            Action = "create_level",
            Params = JsonSerializer.SerializeToElement(new
            {
                summary = Summary(preview),
                levelName = name,
                elevationMm = row.ElevationMm,
                cadFile,
                mode = row.Mode.ToString(),
                cadUnit = CadUnits.First(entry => entry.Value == row.Unit).Key,
                planName = preview.PlanName,
                createsLevel = preview.CreatesLevel,
                // The level with another name at this elevation that is renamed instead of creating a duplicate.
                renameLevelId = preview.RenameFrom is null ? null : RevitJson.Id(preview.RenameFrom.Id),
                createsPlan = preview.CreatesPlan,
                viewTemplateId = row.ViewTemplateId is null ? null : RevitJson.Id(row.ViewTemplateId),
                scale = row.Scale,
            }),
            Tier = problem is null ? PlanTier.Review : PlanTier.Blocked,
            Reasons = reasons,
            Source = JsonSerializer.SerializeToElement(new
            {
                elementId = (preview.ExistingLevel ?? preview.RenameFrom) is { } existing ? RevitJson.Id(existing.Id) : null,
                name,
                category = "Levels",
                cadFile,
            }),
        };
    }

    // Starts rejected: deleting is never the default. The person approves it to delete.
    private static PlanItem DeleteItem(UnusedLevel unused, int index) => new()
    {
        ItemId = "D" + (index + 1).ToString(CultureInfo.InvariantCulture),
        Action = "delete_level",
        Status = PlanItemStatus.Rejected,
        Params = JsonSerializer.SerializeToElement(new
        {
            summary = $"Xoá Level \"{unused.Level.Name}\" ({Elevation(UnitConvert.Length(unused.Level.Elevation))}) "
                + $"và {unused.Views} view của nó",
            levelId = RevitJson.Id(unused.Level.Id),
            levelName = unused.Level.Name,
        }),
        Reasons =
        [
            "Level không dùng tới: không có cấu kiện nào gắn với nó, không view nào nằm trên sheet. "
                + "Mặc định bỏ; duyệt mục này nếu muốn xoá.",
        ],
        Source = JsonSerializer.SerializeToElement(new
        {
            elementId = RevitJson.Id(unused.Level.Id),
            name = unused.Level.Name,
            category = "Levels",
        }),
    };

    // e.g. "Tạo Level +3600 mm · tạo plan "KC - Tầng 2" · import S-201.dwg",
    //      "Đổi tên Level "Level 1" → "Tầng 1" (+0 mm) · đổi tên plan "Level 1" → "KC - Tầng 1"".
    private static string Summary(LevelRowPreview preview)
    {
        var row = preview.Row;
        var elevation = Elevation(row.ElevationMm);
        var parts = new List<string>
        {
            preview.RenameFrom is { } old ? $"Đổi tên Level \"{old.Name}\" → \"{row.LevelName.Trim()}\" ({elevation})"
                : preview.CreatesLevel ? $"Tạo Level {elevation}"
                : $"Level có sẵn {elevation}",
            preview.CreatesPlan ? $"tạo plan \"{preview.PlanName}\""
                : preview.RenamesPlan ? $"đổi tên plan \"{preview.ExistingPlan!.Name}\" → \"{preview.PlanName}\""
                : $"plan có sẵn \"{preview.PlanName}\"",
        };
        if (row.HasCad)
            parts.Add($"{row.Mode.ToString().ToLowerInvariant()} {Path.GetFileName(row.FilePath)}"
                + (row.Unit == ImportUnit.Millimeter ? "" : $" ({CadUnits.First(entry => entry.Value == row.Unit).Key})"));
        return string.Join(" · ", parts);
    }

    private static string Elevation(double mm) => mm.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture) + " mm";
}
