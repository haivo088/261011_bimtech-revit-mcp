using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands.Common;
using Bimtech.RevitMcp.Commands.Plans;
using Bimtech.RevitMcp.Data;
using Bimtech.RevitMcp.Planning;
using Bimtech.RevitMcp.Protocol;
using Bimtech.RevitMcp.Ui;
using Bimtech.StructuralCore.Beams;
using Bimtech.StructuralCore.Families;

namespace Bimtech.RevitMcp.Commands.Structural;

// Beams from clean data (beam/1.0, read from the drawing through InnoCAD) instead of a CAD scan. Axes are from the
// data's grid origin, which sits on the Project Base Point (checked, as for columns). Items are the same create_beams
// items as propose_beams, one per b x h type, each beam keeping its name as Mark. Read-only: saves a plan, no Transaction.
//   - Level: levelId, else the data's meta.level.base by name; beam top at the level plus meta.top_offset_mm.
//   - Type per type name (e.g. 350x600): "types" entry -> existing framing type with matching b/h -> b x h type in
//     templateFamilyId or the company beam family -> Blocked.
//   - Not built: beams the drawing gives no size for (reported in unsized) and strips that are not beams.
public sealed class ProposeBeamsFromDataCommand(PlanService plans, ReviewWindowHost review, ModelChangeTracker changes) : IRevitCommand
{
    private const int MaxReported = 30;

    public string Method => "propose_beams_from_data";
    public CommandAccess Access => CommandAccess.Read;

    public object? Execute(CommandContext context)
    {
        var p = context.Params;
        var document = context.RequireDocument();
        if (document.IsFamilyDocument)
            throw new RpcException(RpcErrorCodes.NotWritable, "The active document is a family. Open a project model.");
        var path = p.RequireString("dataPath", 1024);
        var data = DataFileInput.Read(path, BeamDataReader.Parse);
        var buildable = data.Buildable;
        if (buildable.Count > context.Settings.MaxElementsPerWrite)
            throw RpcException.InvalidParams("dataPath",
                $"the file has {buildable.Count} beams, above the limit of {context.Settings.MaxElementsPerWrite} per call set in BIMTECH MCP Settings.");

        var level = DataPlacement.ResolveBaseLevel(document, p, "levelId", data.Source.LevelBase);
        var assigned = p.GetStringMap("types", maxCount: 200, maxLength: 32);
        StructuralJson.RequireKnownKeys(assigned.Keys, buildable.Select(b => b.Type).Distinct().ToList(), "types");
        var templateFamily = p.Has("templateFamilyId")
            ? StructuralJson.RequireFamily(document, p.RequireElementId("templateFamilyId"), BuiltInCategory.OST_StructuralFraming)
            : DefaultFamilies.Find(document, DefaultFamilies.BeamFamilyName);
        var available = BeamTypeMatcher.CollectBeamTypes(document);

        var basePoint = BasePoint.GetProjectBasePoint(document).Position;
        var items = BeamDataPlanner.BuildItems(
            data, UnitConvert.Length(basePoint.X), UnitConvert.Length(basePoint.Y), RevitJson.Id(level.Id), level.Name,
            (type, b, h) => ChooseType(document, type, b, h, assigned, available, templateFamily),
            DataPlacement.CheckOrigin(document, data.Source.Origin, basePoint));
        var unsized = data.Unsized.Select(b => new { b.Name, b.GridStart, b.GridEnd, b.Width, b.Flags }).ToList();

        var plan = items.Count == 0 ? null : plans.Create(
            PlanStorageLocation.For(document),
            kind: "create_beams",
            title: string.Format(CultureInfo.InvariantCulture, BeamDataText.Title, buildable.Count, level.Name, data.Source.Drawing ?? "?"),
            scope: JsonSerializer.SerializeToElement(new
            {
                dataPath = path,
                source = data.Source,
                level = StructuralJson.Ref(level),
                unsized,
                notBeams = data.NotBeams,
                dataIssues = data.Issues,
            }),
            items: items,
            modelStamp: changes.Current(document));
        if (plan is not null) review.Show(plan, context.UIApplication);

        return new
        {
            plan = plan is null ? null : PlanResults.Status(plan),
            groups = items.Count,
            beams = items.Sum(item => item.Params.GetProperty("beams").GetArrayLength()),
            unsized = unsized.Take(MaxReported),
            unsizedCount = unsized.Count,
            notBeams = data.NotBeams,
            blockedGroups = items.Where(i => i.Tier == PlanTier.Blocked).Select(i => new { i.ItemId, reason = i.Reasons.FirstOrDefault() }),
            level = StructuralJson.Ref(level),
            topOffsetMm = data.Source.TopOffsetMm ?? 0,
            source = data.Source,
            dataIssues = data.Issues,
            reviewWindow = plan is null ? "not opened: nothing to build" : plan.Approval?.Automatic == true ? "not opened: auto-approved" : "opened",
            note = "Nothing was changed in the model. One item per beam type, each beam named in Mark; flagged beams (-xem) "
                + "are left to the reviewer; beams without size (unsized) are not built. No CAD is needed. Tell the user "
                + "about unsized and dataIssues, ask them to review in Revit, then wait_for_plan or get_plan_status and commit_plan.",
        };
    }

    private static BeamTypeChoice ChooseType(
        Document document, string type, double b, double h, IReadOnlyDictionary<string, string> assigned,
        IReadOnlyList<FamilySymbol> available, Family? family)
    {
        var size = $"{b:0}x{h:0}";
        if (assigned.TryGetValue(type, out var typeId))
        {
            var symbol = StructuralJson.RequireType(document, typeId, $"types.{type}", BuiltInCategory.OST_StructuralFraming, "propose_beams_from_data");
            return new BeamTypeChoice(RevitJson.Id(symbol.Id), null, $"Type {symbol.Name}", null);
        }
        if (BeamTypeMatcher.FindExactMatch(b, h, available, s => s) is { } exact)
            return new BeamTypeChoice(RevitJson.Id(exact.Id), null, $"Type {exact.Name} ({exact.FamilyName})", null);
        return family is not null
            ? new BeamTypeChoice(null, RevitJson.Id(family.Id), Format(BeamDataText.TypeInFamily, size, family.Name), null)
            : new BeamTypeChoice(null, null, BeamDataText.NoType, Format(BeamDataText.NoTypeProblem, size));
    }

    private static string Format(string template, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, template, args);
}
