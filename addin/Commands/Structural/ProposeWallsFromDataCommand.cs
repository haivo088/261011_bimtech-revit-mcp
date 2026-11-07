using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands.Common;
using Bimtech.RevitMcp.Commands.Plans;
using Bimtech.RevitMcp.Data;
using Bimtech.RevitMcp.Planning;
using Bimtech.RevitMcp.Protocol;
using Bimtech.RevitMcp.Recognition;
using Bimtech.RevitMcp.Ui;
using Bimtech.StructuralCore.Walls;

namespace Bimtech.RevitMcp.Commands.Structural;

// Walls from clean data (wall/1.0, read from the drawing through InnoCAD) instead of a CAD scan. Centrelines are from
// the data's grid origin, which sits on the Project Base Point (checked, as for columns). Items are the same
// create_walls items as propose_walls. Read-only: saves a plan, no Transaction.
//   - Levels: baseLevelId / topLevelId, else the data's meta.level ("Level 2", top "next" = next level up).
//   - Type per type name (e.g. V300): "types" entry (type ID or "new" for the company type BTC-VachKC_<b>) -> a Basic
//     Wall type of exactly that thickness (Recognition.WallTypes) -> BTC-VachKC_<b> duplicated at commit from
//     templateTypeId or the nearest concrete type -> Blocked.
public sealed class ProposeWallsFromDataCommand(PlanService plans, ReviewWindowHost review, ModelChangeTracker changes) : IRevitCommand
{
    public string Method => "propose_walls_from_data";
    public CommandAccess Access => CommandAccess.Read;

    public object? Execute(CommandContext context)
    {
        var p = context.Params;
        var document = context.RequireDocument();
        if (document.IsFamilyDocument)
            throw new RpcException(RpcErrorCodes.NotWritable, "The active document is a family. Open a project model.");
        var path = p.RequireString("dataPath", 1024);
        var data = DataFileInput.Read(path, WallDataReader.Parse);
        if (data.Walls.Count > context.Settings.MaxElementsPerWrite)
            throw RpcException.InvalidParams("dataPath",
                $"the file has {data.Walls.Count} walls, above the limit of {context.Settings.MaxElementsPerWrite} per call set in BIMTECH MCP Settings.");

        var (baseLevel, topLevel) = DataPlacement.ResolveLevels(document, p, data.Source.LevelBase, data.Source.LevelTop);
        var assigned = StructuralJson.ReadTypeMap(p, maxCount: 200);
        StructuralJson.RequireKnownKeys(assigned.Keys, data.Walls.Select(w => w.Type).Distinct().ToList(), "types");
        var template = p.Has("templateTypeId") ? RequireWallType(document, p.RequireElementId("templateTypeId"), "templateTypeId") : null;
        var pool = WallPlacer.CollectBasicWallTypes(document);

        var basePoint = BasePoint.GetProjectBasePoint(document).Position;
        var items = WallDataPlanner.BuildItems(
            data, UnitConvert.Length(basePoint.X), UnitConvert.Length(basePoint.Y),
            RevitJson.Id(baseLevel.Id), RevitJson.Id(topLevel.Id),
            (type, thickness) => ChooseType(document, type, thickness, assigned, pool, template),
            DataPlacement.CheckOrigin(document, data.Source.Origin, basePoint));

        var plan = plans.Create(
            PlanStorageLocation.For(document),
            kind: "create_walls",
            title: string.Format(CultureInfo.InvariantCulture, WallDataText.Title, data.Walls.Count, baseLevel.Name, topLevel.Name,
                data.Source.Drawing ?? "?"),
            scope: JsonSerializer.SerializeToElement(new
            {
                dataPath = path,
                source = data.Source,
                baseLevel = StructuralJson.Ref(baseLevel),
                topLevel = StructuralJson.Ref(topLevel),
                dataIssues = data.Issues,
                badOutlines = data.BadOutlines,
            }),
            items: items,
            modelStamp: changes.Current(document));
        review.Show(plan, context.UIApplication);

        return new
        {
            plan = PlanResults.Status(plan),
            groups = items.Count,
            walls = items.Sum(item => item.Params.GetProperty("lines").GetArrayLength()),
            blockedGroups = items.Where(i => i.Tier == PlanTier.Blocked).Select(i => new { i.ItemId, reason = i.Reasons.FirstOrDefault() }),
            baseLevel = StructuralJson.Ref(baseLevel),
            topLevel = StructuralJson.Ref(topLevel),
            source = data.Source,
            badOutlines = data.BadOutlines,
            dataIssues = data.Issues,
            reviewWindow = plan.Approval?.Automatic == true ? "not opened: auto-approved" : "opened",
            note = "Nothing was changed in the model. One item per wall type; flagged walls (-xem) are left to the reviewer; "
                + "no CAD is needed. Tell the user about dataIssues, ask them to review in Revit, then wait_for_plan or "
                + "get_plan_status and commit_plan.",
        };
    }

    private static WallTypeChoice ChooseType(
        Document document, string type, double thickness, IReadOnlyDictionary<string, string> assigned,
        IReadOnlyList<WallType> pool, WallType? template)
    {
        var newName = WallTypes.NewTypeName(thickness);
        if (assigned.TryGetValue(type, out var typeId) && typeId != StructuralJson.NewType)
        {
            var chosen = RequireWallType(document, ParseId(typeId, $"types.{type}"), $"types.{type}");
            return new WallTypeChoice(RevitJson.Id(chosen.Id), null, false, Format(WallDataText.TypeExact, chosen.Name), null);
        }
        var companyOnly = typeId == StructuralJson.NewType;
        if (!companyOnly && WallPlacer.SuggestType(thickness, pool, t => t) is ({ } exact, true))
            return new WallTypeChoice(RevitJson.Id(exact.Id), null, false, Format(WallDataText.TypeExact, exact.Name), null);
        var source = template ?? WallPlacer.NearestTemplateType(pool, thickness);
        return source is not null
            ? new WallTypeChoice(null, RevitJson.Id(source.Id), companyOnly, Format(WallDataText.TypeNew, newName, source.Name), null)
            : new WallTypeChoice(null, null, companyOnly, WallDataText.NoTemplate, Format(WallDataText.NoTemplateProblem, type));
    }

    private static WallType RequireWallType(Document document, ElementId id, string param) =>
        document.GetElement(id) is WallType { Kind: WallKind.Basic } type
            ? type
            : throw RpcException.InvalidParams(param, "is not a Basic Wall type.");

    private static ElementId ParseId(string text, string param) =>
        long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id)
            ? new ElementId(id)
            : throw RpcException.InvalidParams(param, "expected a type ID such as \"12345\".");

    private static string Format(string template, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, template, args);
}
