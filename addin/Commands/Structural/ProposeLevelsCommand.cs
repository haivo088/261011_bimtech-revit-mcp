using System.Globalization;
using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands.Plans;
using Bimtech.RevitMcp.Planning;
using Bimtech.RevitMcp.Protocol;
using Bimtech.RevitMcp.Ui;
using Bimtech.StructuralCore.Levels;

namespace Bimtech.RevitMcp.Commands.Structural;

// Step 1 of setting up a structural model: one plan item per storey that creates the level, its
// structural plan "KC - <name>" and imports or links that storey's CAD at the origin. Read-only:
// it checks every storey with LevelSetup.Preview and saves a plan, but never opens a Transaction.
// Grids come later from propose_grids, because they are read from CAD that must already be in the model.
public sealed class ProposeLevelsCommand(PlanService plans, ReviewWindowHost review, ModelChangeTracker changes) : IRevitCommand
{
    public string Method => "propose_levels";
    // Saves a plan file but never changes the model.
    public CommandAccess Access => CommandAccess.Read;

    private const int MaxLevels = 100;

    public object? Execute(CommandContext context)
    {
        var document = context.RequireDocument();
        if (document.IsFamilyDocument)
            throw new RpcException(RpcErrorCodes.NotWritable, "The active document is a family. Open a project model.");

        var input = context.Params.GetObjectList("levels", MaxLevels);
        if (input.Count == 0) throw RpcException.InvalidParams("levels", "give at least one storey.");
        var rows = input.Select(ReadRow).ToList();

        var hasPlanType = LevelSetup.FindStructuralPlanType(document) is not null;
        var items = new List<PlanItem>();
        var unchanged = new List<string>();
        foreach (var (preview, index) in LevelSetup.Preview(document, rows).Select((preview, index) => (preview, index)))
        {
            context.Cancellation.ThrowIfCancellationRequested();
            if (preview.Problem is null && !preview.CreatesLevel && !preview.CreatesPlan && !preview.Row.HasCad)
                unchanged.Add(preview.Row.LevelName.Trim());
            else
                items.Add(ToItem(document, preview, index, hasPlanType));
        }
        if (items.Count == 0)
            return new
            {
                plan = (object?)null,
                unchangedLevels = unchanged,
                note = "Every storey already has its level and structural plan, and no CAD file was given. No plan was created.",
            };

        var plan = plans.Create(
            PlanStorageLocation.For(document),
            kind: "create_levels",
            title: $"Tạo {items.Count} tầng (Level, Structural Plan, CAD)",
            scope: JsonSerializer.SerializeToElement(new
            {
                levels = rows.Select(row => new { name = row.LevelName.Trim(), elevationMm = row.ElevationMm }),
                unchangedLevels = unchanged,
            }),
            items: items,
            modelStamp: changes.Current(document));
        review.Show(plan, context.UIApplication);

        return new
        {
            plan = PlanResults.Status(plan),
            unchangedLevels = unchanged,
            reviewWindow = "opened",
            note = "Nothing was changed in the model. The review window is open in Revit; ask the user to review there "
                + "and say when they are done, then call get_plan_status and commit_plan. After the commit, CAD sits at "
                + "the origin: call list_cad_sources and propose_grids to build grids and align every storey's CAD.",
        };
    }

    private static LevelSetupRow ReadRow(CommandParams level, int index)
    {
        var name = level.RequireString("name", 255).Trim();
        if (name.Length == 0) throw RpcException.InvalidParams($"levels[{index}].name", "must not be empty.");
        var cadFile = level.GetString("cadFile", 1024)?.Trim();
        if (!string.IsNullOrEmpty(cadFile) && !string.Equals(Path.GetExtension(cadFile), ".dwg", StringComparison.OrdinalIgnoreCase))
            throw RpcException.InvalidParams($"levels[{index}].cadFile", "must be a .dwg file.");
        return new LevelSetupRow(
            cadFile ?? "",
            name,
            level.RequireDouble("elevationMm", -100_000, 1_000_000),
            level.GetEnum("mode", CadPlacementMode.Import),
            CreateGrids: false);
    }

    // Problems become Blocked items with a reason instead of errors, so one bad storey does not hide the rest.
    private static PlanItem ToItem(Document document, LevelRowPreview preview, int index, bool hasPlanType)
    {
        var row = preview.Row;
        var name = row.LevelName.Trim();
        var reasons = new List<string>();
        var problem = preview.Problem
            ?? (preview.CreatesPlan && !hasPlanType ? "Model chưa có loại view Structural Plan." : null);
        if (problem is not null) reasons.Add(problem);
        if (!preview.CreatesLevel) reasons.Add("Level đã có, dùng lại.");
        if (row.HasCad && preview.ExistingPlan is { } plan && LevelSetup.PlanHasCadFile(document, plan, row.FilePath))
            reasons.Add("Plan này đã có CAD cùng tên file; nhập nữa sẽ thành 2 bản.");

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
                planName = preview.ExistingPlan?.Name ?? preview.PlanName,
                createsLevel = preview.CreatesLevel,
                createsPlan = preview.CreatesPlan,
            }),
            Tier = problem is null ? PlanTier.Review : PlanTier.Blocked,
            Reasons = reasons,
            Source = JsonSerializer.SerializeToElement(new
            {
                elementId = preview.ExistingLevel is null ? null : RevitJson.Id(preview.ExistingLevel.Id),
                name,
                category = "Levels",
                cadFile,
            }),
        };
    }

    // e.g. "Tạo Level +3600 mm · tạo plan "KC - Tầng 2" · import S-201.dwg".
    private static string Summary(LevelRowPreview preview)
    {
        var row = preview.Row;
        var parts = new List<string>
        {
            (preview.CreatesLevel ? "Tạo Level " : "Level có sẵn ")
                + row.ElevationMm.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture) + " mm",
            preview.CreatesPlan ? $"tạo plan \"{preview.PlanName}\"" : $"plan có sẵn \"{preview.ExistingPlan!.Name}\"",
        };
        if (row.HasCad) parts.Add($"{row.Mode.ToString().ToLowerInvariant()} {Path.GetFileName(row.FilePath)}");
        return string.Join(" · ", parts);
    }
}
