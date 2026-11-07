using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands.Common;
using Bimtech.RevitMcp.Commands.Plans;
using Bimtech.RevitMcp.Data;
using Bimtech.RevitMcp.Planning;
using Bimtech.RevitMcp.Protocol;
using Bimtech.RevitMcp.Ui;

namespace Bimtech.RevitMcp.Commands.Structural;

// Grids from clean data (grid/1.0) instead of a CAD import: no CAD in the model, no recognition, no alignment.
// The data's origin (e.g. A-1) sits on the Project Base Point. Items are the same create_grid items as
// propose_grids, so the review window, preview and commit_plan are shared. Read-only: saves a plan, no Transaction.
public sealed class ProposeGridsFromDataCommand(PlanService plans, ReviewWindowHost review, ModelChangeTracker changes) : IRevitCommand
{
    public string Method => "propose_grids_from_data";
    public CommandAccess Access => CommandAccess.Read;

    private const long MaxFileBytes = 5_000_000;

    public object? Execute(CommandContext context)
    {
        var p = context.Params;
        var document = context.RequireDocument();
        var path = p.RequireString("dataPath", 1024);
        var pin = p.GetBool("pin", true);
        var data = Read(path);

        var basePoint = BasePoint.GetProjectBasePoint(document).Position;
        var (baseX, baseY) = (UnitConvert.Length(basePoint.X), UnitConvert.Length(basePoint.Y));
        var existing = new FilteredElementCollector(document).OfClass(typeof(Grid)).Cast<Grid>()
            .Select(grid => grid.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var items = GridDataPlanner.BuildItems(data, baseX, baseY, existing, pin);
        var (gridMargin, levelMargin) = DatumExtentsItem.ReadMargins(p);
        var levels = new FilteredElementCollector(document).OfClass(typeof(Level)).Cast<Level>()
            .Select(level => (level.Name, UnitConvert.Length(level.Elevation))).ToList();
        var newGrids = items.Count(item => item.Tier != PlanTier.Blocked);
        if (DatumExtentsItem.Create(levels, existing.Count + newGrids, gridMargin, levelMargin) is { } fit) items.Add(fit);

        var plan = plans.Create(
            PlanStorageLocation.For(document),
            kind: "create_grids",
            title: string.Format(GridDataText.Title, data.Grids.Count, data.Source.Src ?? Path.GetFileName(path)),
            scope: JsonSerializer.SerializeToElement(new
            {
                dataPath = path,
                source = data.Source,
                dataIssues = data.Issues,
                dataDecisions = data.Decisions,
                selfCheck = data.SelfCheck,
            }),
            items: items,
            modelStamp: changes.Current(document));
        review.Show(plan, context.UIApplication);

        return new
        {
            plan = PlanResults.Status(plan),
            grids = new
            {
                vertical = data.Grids.Count(g => g.IsVertical),
                horizontal = data.Grids.Count(g => !g.IsVertical),
                blocked = items.Where(item => item.Tier == PlanTier.Blocked).Select(item => item.ItemId),
            },
            source = data.Source,
            selfCheck = data.SelfCheck,
            dataIssues = data.Issues,
            dataDecisions = data.Decisions,
            reviewWindow = "opened",
            note = "Nothing was changed in the model. The data's origin (" + (data.Source.Origin ?? "first grids")
                + ") sits on the Project Base Point; no CAD is needed. Tell the user about dataIssues, ask them to review "
                + "in Revit, then call wait_for_plan or get_plan_status and commit_plan.",
        };
    }

    private static GridDataSet Read(string path)
    {
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw RpcException.InvalidParams("dataPath", "expected a .json file (grid/1.0).");
        if (!File.Exists(path))
            throw RpcException.InvalidParams("dataPath", $"file not found: {path}");
        if (new FileInfo(path).Length > MaxFileBytes)
            throw RpcException.InvalidParams("dataPath", $"file is larger than {MaxFileBytes} bytes.");
        try
        {
            return GridDataReader.Parse(File.ReadAllText(path));
        }
        catch (GridDataException exception)
        {
            throw RpcException.InvalidParams("dataPath", exception.Message);
        }
    }
}
