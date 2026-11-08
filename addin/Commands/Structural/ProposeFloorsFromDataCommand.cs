using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands.Common;
using Bimtech.RevitMcp.Commands.Plans;
using Bimtech.RevitMcp.Data;
using Bimtech.RevitMcp.Planning;
using Bimtech.RevitMcp.Protocol;
using Bimtech.RevitMcp.Ui;
using Bimtech.StructuralCore.Floors;

namespace Bimtech.RevitMcp.Commands.Structural;

// Slabs from clean data (slab/1.0, read from the drawing through InnoCAD) instead of tracing beams in the model.
// Outlines are from the data's grid origin, which sits on the Project Base Point (checked, as for columns). Items are
// create_floor items, one per slab, with its openings as holes. Read-only: saves a plan, no Transaction.
//   - Level: levelId, else the data's meta.level.base by name; slab top at the level plus the slab's top_offset.
//   - Floor type: floorTypeId -> BTC_SanKC_<thickness> -> BTC_SanKC_<thickness> created at commit from a floor type with
//     layers -> Blocked.
public sealed class ProposeFloorsFromDataCommand(PlanService plans, ReviewWindowHost review, ModelChangeTracker changes) : IRevitCommand
{
    public string Method => "propose_floors_from_data";
    public CommandAccess Access => CommandAccess.Read;

    public object? Execute(CommandContext context)
    {
        var p = context.Params;
        var document = context.RequireDocument();
        if (document.IsFamilyDocument)
            throw new RpcException(RpcErrorCodes.NotWritable, "The active document is a family. Open a project model.");
        var path = p.RequireString("dataPath", 1024);
        var data = DataFileInput.Read(path, SlabDataReader.Parse);

        var level = DataPlacement.ResolveBaseLevel(document, p, "levelId", data.Source.LevelBase);
        var assigned = p.Has("floorTypeId")
            ? document.GetElement(p.RequireElementId("floorTypeId")) as FloorType
                ?? throw RpcException.InvalidParams("floorTypeId", "is not a floor type.")
            : null;
        var canCreate = FloorDefaults.FindSeedType(document) is not null;

        var basePoint = BasePoint.GetProjectBasePoint(document).Position;
        var items = SlabDataPlanner.BuildItems(
            data, UnitConvert.Length(basePoint.X), UnitConvert.Length(basePoint.Y), RevitJson.Id(level.Id), level.Name,
            thickness => ChooseType(document, thickness, assigned, canCreate),
            DataPlacement.CheckOrigin(document, data.Source.Origin, basePoint));

        var plan = plans.Create(
            PlanStorageLocation.For(document),
            kind: "create_floor",
            title: string.Format(CultureInfo.InvariantCulture, SlabDataText.Title, data.Slabs.Count, level.Name, data.Source.Drawing ?? "?"),
            scope: JsonSerializer.SerializeToElement(new
            {
                dataPath = path,
                source = data.Source,
                level = StructuralJson.Ref(level),
                dataIssues = data.Issues,
            }),
            items: items,
            modelStamp: changes.Current(document));
        review.Show(plan, context.UIApplication);

        return new
        {
            plan = PlanResults.Status(plan),
            slabs = items.Count,
            holes = items.Sum(i => i.Params.GetProperty("holes").GetArrayLength()),
            notCut = data.Openings.Where(o => !o.CanCut).Select(o => new { o.Id, o.GridRef }),
            blockedGroups = items.Where(i => i.Tier == PlanTier.Blocked).Select(i => new { i.ItemId, reason = i.Reasons.FirstOrDefault() }),
            level = StructuralJson.Ref(level),
            source = data.Source,
            dataIssues = data.Issues,
            reviewWindow = plan.Approval?.Automatic == true ? "not opened: auto-approved" : "opened",
            note = "Nothing was changed in the model. One item per slab with its openings as holes; no CAD is needed. "
                + "Tell the user about dataIssues and notCut, ask them to review in Revit, then wait_for_plan or "
                + "get_plan_status and commit_plan.",
        };
    }

    private static SlabTypeChoice ChooseType(Document document, double thickness, FloorType? assigned, bool canCreate)
    {
        if (assigned is not null)
            return new SlabTypeChoice(RevitJson.Id(assigned.Id), Format(SlabDataText.TypeExact, assigned.Name), null);
        if (FloorDefaults.FindCompanyType(document, thickness) is { } company)
            return new SlabTypeChoice(RevitJson.Id(company.Id), Format(SlabDataText.TypeExact, company.Name), null);
        var name = FloorDefaults.CompanyTypeName(thickness);
        return canCreate
            ? new SlabTypeChoice(null, Format(SlabDataText.TypeNew, name), null)
            : new SlabTypeChoice(null, SlabDataText.NoType, Format(SlabDataText.NoTypeProblem, name));
    }

    private static string Format(string template, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, template, args);
}
