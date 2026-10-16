using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands.Common;
using Bimtech.RevitMcp.Commands.Plans;
using Bimtech.RevitMcp.Planning;
using Bimtech.RevitMcp.Protocol;
using Bimtech.RevitMcp.Ui;
using Bimtech.StructuralCore.Levels;

namespace Bimtech.RevitMcp.Commands.Structural;

// Step 2 of setting up a structural model, after propose_levels is committed: reads the grid layer of one
// CAD (GridBuilder.Plan) and proposes one item per grid line, plus one item per CAD to move so grid 1-A of
// the drawing lands on the Project Base Point. Read-only: it saves a plan but never opens a Transaction.
public sealed class ProposeGridsCommand(PlanService plans, ReviewWindowHost review, ModelChangeTracker changes) : IRevitCommand
{
    public string Method => "propose_grids";
    // Saves a plan file but never changes the model.
    public CommandAccess Access => CommandAccess.Read;

    private const int MaxAlignedCad = 200;
    // Below this the CAD already sits where it should; no move item is proposed.
    private const double MinMoveMm = 0.5;

    public object? Execute(CommandContext context)
    {
        var document = context.RequireDocument();
        var cad = StructuralJson.RequireCad(document, context.Params.RequireElementId("cadId"), "cadId");
        var view = StructuralJson.RequirePlanView(context, document, cad);
        var aligned = context.Params.GetElementIds("alignCadIds", MaxAlignedCad)
            .Select((id, index) => StructuralJson.RequireCad(document, id, $"alignCadIds[{index}]"))
            .ToList();

        GridBuilder.GridPlan gridPlan;
        try
        {
            gridPlan = GridBuilder.Plan(document, view, cad);
        }
        catch (InvalidOperationException exception)
        {
            throw RpcException.InvalidParams("cadId", exception.Message);
        }

        var dxMm = UnitConvert.Length(gridPlan.AlignmentTranslation.X);
        var dyMm = UnitConvert.Length(gridPlan.AlignmentTranslation.Y);
        var items = new List<PlanItem>();
        if (Math.Sqrt(dxMm * dxMm + dyMm * dyMm) >= MinMoveMm)
        {
            var targets = new[] { cad }.Concat(aligned).DistinctBy(target => target.Id).ToList();
            items.AddRange(targets.Select((target, index) => MoveItem(document, target, index, dxMm, dyMm)));
        }
        var existing = gridPlan.ExistingNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        items.AddRange(gridPlan.Grids.Select(grid => GridItem(grid, existing.Contains(grid.Name))));

        var plan = plans.Create(
            PlanStorageLocation.For(document),
            kind: "create_grids",
            title: $"Dựng {gridPlan.Grids.Count} trục từ CAD layer \"{gridPlan.SourceLayer}\"",
            scope: JsonSerializer.SerializeToElement(new
            {
                cad = StructuralJson.CadRef(document, cad),
                view = StructuralJson.Ref(view),
                sourceLayer = gridPlan.SourceLayer,
                moveMm = new { x = dxMm, y = dyMm },
                alignedCad = aligned.Select(target => StructuralJson.CadRef(document, target)),
            }),
            items: items,
            modelStamp: changes.Current(document));
        review.Show(plan, context.UIApplication);

        return new
        {
            plan = PlanResults.Status(plan),
            sourceLayer = gridPlan.SourceLayer,
            grids = gridPlan.Grids.Count,
            existingGridNames = gridPlan.ExistingNames,
            moveMm = new { x = dxMm, y = dyMm },
            reviewWindow = "opened",
            note = "Nothing was changed in the model. Grids are placed so grid 1-A sits on the Project Base Point; "
                + "the move items shift the CAD to match. Grids whose name already exists are Blocked. "
                + "Ask the user to review in Revit and say when they are done, then call get_plan_status and commit_plan.",
        };
    }

    private static PlanItem MoveItem(Document document, ImportInstance cad, int index, double dxMm, double dyMm)
    {
        var name = ElementQuery.SafeName(document.GetElement(cad.GetTypeId()) ?? cad);
        return new PlanItem
        {
            ItemId = "M" + (index + 1).ToString(CultureInfo.InvariantCulture),
            Action = "move_cad",
            Params = JsonSerializer.SerializeToElement(new
            {
                summary = $"Dời CAD {Mm(dxMm)}, {Mm(dyMm)} mm để trục 1-A trùng Project Base Point",
                cadId = RevitJson.Id(cad.Id),
                dxMm,
                dyMm,
            }),
            Reasons = cad.Pinned ? ["CAD đang ghim: bỏ ghim để dời rồi ghim lại."] : [],
            Source = JsonSerializer.SerializeToElement(new
            {
                elementId = RevitJson.Id(cad.Id),
                name,
                category = "CAD",
            }),
        };
    }

    private static PlanItem GridItem(GridBuilder.PlannedGrid grid, bool nameTaken)
    {
        var start = grid.Line.GetEndPoint(0);
        var end = grid.Line.GetEndPoint(1);
        var position = grid.IsVertical ? $"x = {Mm(UnitConvert.Length(start.X))}" : $"y = {Mm(UnitConvert.Length(start.Y))}";
        return new PlanItem
        {
            ItemId = "G-" + grid.Name,
            Action = "create_grid",
            Params = JsonSerializer.SerializeToElement(new
            {
                summary = $"Trục {grid.Name} ({(grid.IsVertical ? "đứng" : "ngang")}): {position} mm, "
                    + $"dài {(UnitConvert.Length(grid.Line.Length) / 1000).ToString("0.##", CultureInfo.InvariantCulture)} m",
                name = grid.Name,
                startMm = new { x = UnitConvert.Length(start.X), y = UnitConvert.Length(start.Y) },
                endMm = new { x = UnitConvert.Length(end.X), y = UnitConvert.Length(end.Y) },
                direction = grid.IsVertical ? "vertical" : "horizontal",
            }),
            Tier = nameTaken ? PlanTier.Blocked : PlanTier.Review,
            Reasons = nameTaken ? [$"Model đã có Grid tên '{grid.Name}'."] : [],
            Source = JsonSerializer.SerializeToElement(new { name = grid.Name, category = "Grids" }),
        };
    }

    private static string Mm(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
