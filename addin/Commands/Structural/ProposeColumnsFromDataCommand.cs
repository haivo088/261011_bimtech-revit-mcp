using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands.Common;
using Bimtech.RevitMcp.Commands.Plans;
using Bimtech.RevitMcp.Data;
using Bimtech.RevitMcp.Planning;
using Bimtech.RevitMcp.Protocol;
using Bimtech.RevitMcp.Ui;
using Bimtech.StructuralCore.Columns;
using Bimtech.StructuralCore.Families;

namespace Bimtech.RevitMcp.Commands.Structural;

// Columns from clean data (column/1.0) instead of a CAD scan. Positions are from the data's grid origin, which sits on
// the Project Base Point (as propose_grids_from_data built it); the command checks that the model's origin grids are
// there. Items are the same create_columns items as propose_columns. Read-only: saves a plan, no Transaction.
//   - Levels: baseLevelId / topLevelId, else the data's meta.level ("Level 2", top "next" = next level up).
//   - Type per type name: "types" entry (type ID or "new") -> existing type with matching b/h -> b x h type in
//     templateFamilyId or the company family BTC-CotCN_BeTong -> Blocked.
public sealed class ProposeColumnsFromDataCommand(PlanService plans, ReviewWindowHost review, ModelChangeTracker changes) : IRevitCommand
{
    public string Method => "propose_columns_from_data";
    public CommandAccess Access => CommandAccess.Read;


    public object? Execute(CommandContext context)
    {
        var p = context.Params;
        var document = context.RequireDocument();
        if (document.IsFamilyDocument)
            throw new RpcException(RpcErrorCodes.NotWritable, "The active document is a family. Open a project model.");
        var data = DataFileInput.Read(p.RequireString("dataPath", 1024), ColumnDataReader.Parse);
        if (data.Columns.Count > context.Settings.MaxElementsPerWrite)
            throw RpcException.InvalidParams("dataPath",
                $"the file has {data.Columns.Count} columns, above the limit of {context.Settings.MaxElementsPerWrite} per call set in BIMTECH MCP Settings.");

        var (baseLevel, topLevel) = DataPlacement.ResolveLevels(document, p, data.Source.LevelBase, data.Source.LevelTop);
        var assigned = StructuralJson.ReadTypeMap(p, maxCount: 200);
        StructuralJson.RequireKnownKeys(assigned.Keys, data.Columns.Select(c => c.Type).Distinct().ToList(), "types");
        var templateFamily = p.Has("templateFamilyId")
            ? StructuralJson.RequireFamily(document, p.RequireElementId("templateFamilyId"), BuiltInCategory.OST_StructuralColumns)
            : DefaultFamilies.Find(document, DefaultFamilies.ColumnFamilyName);
        var available = ColumnTypeMatcher.CollectColumnTypes(document);

        var basePoint = BasePoint.GetProjectBasePoint(document).Position;
        var (baseX, baseY) = (UnitConvert.Length(basePoint.X), UnitConvert.Length(basePoint.Y));
        var existing = new ColumnPlacer.DuplicateChecker(document, baseLevel, topLevel);
        var result = ColumnDataPlanner.BuildItems(
            data, baseX, baseY, RevitJson.Id(baseLevel.Id), RevitJson.Id(topLevel.Id),
            (type, b, h) => ChooseType(document, type, b, h, assigned, available, templateFamily),
            (x, y) => existing.Find(new XYZ(Feet(x), Feet(y), 0)) is not null,
            DataPlacement.CheckOrigin(document, data.Source.Origin, basePoint));

        if (result.Items.Count == 0)
            return new
            {
                plan = (object?)null,
                skippedExisting = result.SkippedExisting,
                hidden = result.Hidden.Count,
                note = "Every column of this file already exists in the model (or is hidden). No plan was created.",
            };

        var plan = plans.Create(
            PlanStorageLocation.For(document),
            kind: "create_columns",
            title: string.Format(CultureInfo.InvariantCulture, ColumnDataText.Title,
                data.Columns.Count - result.Hidden.Count, baseLevel.Name, topLevel.Name, data.Source.Src ?? "?"),
            scope: JsonSerializer.SerializeToElement(new
            {
                dataPath = p.RequireString("dataPath", 1024),
                source = data.Source,
                baseLevel = StructuralJson.Ref(baseLevel),
                topLevel = StructuralJson.Ref(topLevel),
                dataIssues = data.Issues,
                emptyCrossings = data.EmptyCrossings,
                hidden = result.Hidden.Select(c => c.GridRef),
            }),
            items: result.Items,
            modelStamp: changes.Current(document));
        review.Show(plan, context.UIApplication);

        return new
        {
            plan = PlanResults.Status(plan),
            groups = result.Items.Count,
            columns = result.Items.Sum(item => item.Params.GetProperty("columns").GetArrayLength()),
            blockedGroups = result.Items.Where(i => i.Tier == PlanTier.Blocked).Select(i => new { i.ItemId, reason = i.Reasons.FirstOrDefault() }),
            baseLevel = StructuralJson.Ref(baseLevel),
            topLevel = StructuralJson.Ref(topLevel),
            skippedExisting = result.SkippedExisting,
            hidden = result.Hidden.Select(c => c.GridRef),
            source = data.Source,
            dataChecked = data.IsChecked,
            dataIssues = data.Issues,
            emptyCrossings = data.EmptyCrossings,
            wallLikeLeftForWalls = data.WallLike,
            reviewWindow = plan.Approval?.Automatic == true ? "not opened: auto-approved" : "opened",
            note = "Nothing was changed in the model. One item per column type; no CAD is needed. Tell the user about "
                + "dataIssues and the empty grid crossings, ask them to review in Revit, then wait_for_plan or "
                + "get_plan_status and commit_plan.",
        };
    }

    private static ColumnTypeChoice ChooseType(
        Document document, string type, double b, double h, IReadOnlyDictionary<string, string> assigned,
        IReadOnlyList<FamilySymbol> available, Family? family)
    {
        var size = $"{b:0}x{h:0}";
        if (assigned.TryGetValue(type, out var typeId) && typeId != StructuralJson.NewType)
        {
            var symbol = StructuralJson.RequireType(document, typeId, $"types.{type}", BuiltInCategory.OST_StructuralColumns, "propose_columns_from_data");
            return new ColumnTypeChoice(RevitJson.Id(symbol.Id), null, $"Type {symbol.Name}", null);
        }
        if (typeId is null && ColumnTypeMatcher.FindExactMatch(b, h, available, s => s) is { } exact)
            return new ColumnTypeChoice(RevitJson.Id(exact.Id), null, $"Type {exact.Name} ({exact.FamilyName})", null);
        return family is not null
            ? new ColumnTypeChoice(null, RevitJson.Id(family.Id), Format(ColumnDataText.TypeInFamily, size, family.Name), null)
            : new ColumnTypeChoice(null, null, ColumnDataText.NoType, Format(ColumnDataText.NoTypeProblem, size));
    }

    private static string Format(string template, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, template, args);

    private static double Feet(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
}
