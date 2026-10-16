using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands.Common;
using Bimtech.RevitMcp.Commands.Plans;
using Bimtech.RevitMcp.Planning;
using Bimtech.RevitMcp.Protocol;
using Bimtech.RevitMcp.Ui;
using Bimtech.StructuralCore.Walls;

namespace Bimtech.RevitMcp.Commands.Structural;

// Plans structural walls from a scan_cad_walls scan: one review item per wall group (tag name + thickness, or for
// walls recognized from hatches: evidence level + thickness, with the evidence counted), placed between two levels. Read-only: it may drop unsupported walls and extend walls to columns in memory,
// checks each group against existing walls (WallPlacer.PlanPlacement) and saves a plan, but never opens a Transaction.
// Type per group: "types" entry → existing Basic Wall type of that thickness → new type "BTC-VachKC_<b>"
// duplicated at commit time from templateTypeId (default: the Basic Wall type closest in thickness).
public sealed class ProposeWallsCommand(PlanService plans, ReviewWindowHost review, ModelChangeTracker changes) : IRevitCommand
{
    public string Method => "propose_walls";
    // Saves a plan file but never changes the model.
    public CommandAccess Access => CommandAccess.Read;

    private sealed record GroupPlan(
        WallSizeGroup Group, WallType? Type, string TypeSource, WallType? Template, string? NewTypeName, string? Problem);

    public object? Execute(CommandContext context)
    {
        var p = context.Params;
        var document = context.RequireDocument();
        if (document.IsFamilyDocument)
            throw new RpcException(RpcErrorCodes.NotWritable, "The active document is a family. Open a project model.");
        var scanId = p.RequireString("scanId", maxLength: 64);
        var scan = context.Sessions.Require<WallScan>(WallScan.Kind, scanId, document, "scanId");
        var baseLevel = StructuralJson.RequireLevel(document, p.RequireElementId("baseLevelId"), "baseLevelId");
        var topLevel = StructuralJson.RequireLevel(document, p.RequireElementId("topLevelId"), "topLevelId");
        var baseOffsetMm = p.GetDouble("baseOffsetMm", 0, -100_000, 100_000);
        var topOffsetMm = p.GetDouble("topOffsetMm", 0, -100_000, 100_000);
        if (topLevel.Elevation + Feet(topOffsetMm) - baseLevel.Elevation - Feet(baseOffsetMm) <= Feet(1))
            throw RpcException.InvalidParams("topLevelId", "the top (with its offset) must be higher than the base (with its offset).");
        var tagParameterName = p.GetString("tagParameterName", maxLength: 200);

        var include = p.GetStringList("include", maxCount: 500, maxLength: 200);
        var exclude = p.GetStringList("exclude", maxCount: 500, maxLength: 200);
        var assigned = p.GetStringMap("types", maxCount: 500, maxLength: 32);
        StructuralJson.RequireKnownKeys(include, scan.GroupKeys, "include");
        StructuralJson.RequireKnownKeys(exclude, scan.GroupKeys, "exclude");
        StructuralJson.RequireKnownKeys(assigned.Keys, scan.GroupKeys, "types");
        var template = p.Has("templateTypeId") ? RequireWallType(document, p.RequireElementId("templateTypeId"), "templateTypeId") : null;
        if (template is not null && template.GetCompoundStructure() is null)
            throw RpcException.InvalidParams("templateTypeId", "has no layer structure to resize.");

        // Same optional steps as the WPF tool's placement: drop walls with nothing below, then extend to columns.
        var walls = scan.Walls.ToList();
        var dropped = 0;
        if (p.GetBool("onlySupported", false))
        {
            var kept = WallScanPipeline.KeepSupported(document, walls, baseLevel.Elevation + Feet(baseOffsetMm));
            dropped = walls.Count - kept.Count;
            walls = kept;
        }
        var extended = 0;
        if (p.GetBool("extendToColumns", false))
        {
            var columnSearchMm = p.GetDouble("columnSearchMm", WallScanPipeline.DefaultColumnSearchDistanceMm, 1, 20000);
            var extension = WallColumnExtender.Extend(document, walls, baseLevel.Elevation, Feet(columnSearchMm));
            walls = extension.Walls;
            extended = extension.ExtendedWallCount;
        }
        context.Cancellation.ThrowIfCancellationRequested();

        var groups = scan.FromHatch ? WallPlacer.GroupRecognizedWalls(walls) : WallPlacer.GroupWalls(walls);
        var selected = groups
            .Where(group => (group.ModelByDefault || include.Contains(group.Key)) && !exclude.Contains(group.Key))
            .ToList();
        if (selected.Count == 0)
            throw RpcException.InvalidParams("include",
                "no group is selected. Groups not proposed by default (\"-maybe\" groups, groups without a tag name) must be listed in 'include'"
                + (dropped > 0 ? $"; onlySupported dropped {dropped} wall(s) with nothing below." : "."));
        var total = selected.Sum(group => group.Walls.Count);
        var limit = context.Settings.MaxElementsPerWrite;
        if (total > limit)
            throw RpcException.InvalidParams("scanId",
                $"the selected groups have {total} walls, above the limit of {limit} per call set in BIMTECH MCP Settings. "
                + "Exclude some groups, scan a smaller region, or ask the user to raise the limit.");

        var groupPlans = Resolve(document, selected, assigned, template);
        // A future type has the thickness of its group; duplicates only depend on position, so the template stands in.
        var placement = WallPlacer.PlanPlacement(document,
            groupPlans.Select(plan => new WallAssignment(plan.Group, plan.Type ?? plan.Template)).ToList(),
            baseLevel, topLevel, Feet(baseOffsetMm), Feet(topOffsetMm));

        var items = new List<PlanItem>();
        var alreadyModeled = new List<string>();
        foreach (var (plan, preview) in groupPlans.Zip(placement))
        {
            var problem = plan.Problem ?? preview.Problem;
            if (problem is null && preview.WallsToPlace == 0) alreadyModeled.Add(plan.Group.Key);
            else items.Add(ToItem(plan, preview, problem, baseLevel, topLevel, baseOffsetMm, topOffsetMm, tagParameterName));
        }
        if (items.Count == 0)
            return new
            {
                plan = (object?)null,
                alreadyModeledGroups = alreadyModeled,
                note = "Every selected wall already exists in the model. No plan was created.",
            };

        var created = plans.Create(
            PlanStorageLocation.For(document),
            kind: "create_walls",
            title: $"Dựng vách {items.Count} nhóm, {baseLevel.Name} → {topLevel.Name}",
            scope: JsonSerializer.SerializeToElement(new
            {
                scanId,
                cadId = RevitJson.Id(scan.CadId),
                baseLevel = StructuralJson.Ref(baseLevel),
                topLevel = StructuralJson.Ref(topLevel),
                baseOffsetMm,
                topOffsetMm,
                droppedUnsupported = dropped,
                extendedToColumns = extended,
                alreadyModeledGroups = alreadyModeled,
            }),
            items: items,
            modelStamp: changes.Current(document));
        review.Show(created, context.UIApplication);

        return new
        {
            plan = PlanResults.Status(created),
            wallsToPlace = placement.Sum(preview => preview.WallsToPlace),
            alreadyModeledGroups = alreadyModeled,
            droppedUnsupported = dropped,
            extendedToColumns = extended,
            newTypes = groupPlans.Where(plan => plan.NewTypeName is not null).Select(plan => plan.NewTypeName).Distinct().ToList(),
            reviewWindow = "opened",
            note = "Nothing was changed in the model. Each item is one wall group. Ask the user to review in Revit and say "
                + "when they are done, then call get_plan_status and commit_plan.",
        };
    }

    private static List<GroupPlan> Resolve(
        Document document, List<WallSizeGroup> groups, IReadOnlyDictionary<string, string> assigned, WallType? template)
    {
        var pool = WallPlacer.CollectBasicWallTypes(document);
        var needTypes = new List<WallSizeGroup>();
        var byGroup = new Dictionary<WallSizeGroup, GroupPlan>();
        foreach (var group in groups)
        {
            var (suggested, isExact) = WallPlacer.SuggestType(group.SizeBMm, pool, type => type);
            if (assigned.TryGetValue(group.Key, out var typeId))
                byGroup[group] = new GroupPlan(group, RequireWallType(document, ParseId(typeId, $"types.{group.Key}"), $"types.{group.Key}"),
                    "assigned", null, null, null);
            else if (isExact)
                byGroup[group] = new GroupPlan(group, suggested, "exact", null, null, null);
            else
                needTypes.Add(group);
        }

        // Groups of the same thickness share one new type, as WallPlacer.PlanMissingTypes names them.
        foreach (var (group, reuse, newTypeName) in WallPlacer.PlanMissingTypes(needTypes, pool))
        {
            var groupTemplate = template ?? WallPlacer.NearestTemplateType(pool, group.SizeBMm);
            byGroup[group] = reuse is not null ? new GroupPlan(group, reuse, "exact", null, null, null)
                : groupTemplate is null
                    ? new GroupPlan(group, null, "create", null, null, "Model chưa có Basic Wall Type có cấu tạo lớp để làm mẫu.")
                    : new GroupPlan(group, null, "create", groupTemplate, newTypeName, null);
        }
        return groups.Select(group => byGroup[group]).ToList();
    }

    // Problems become Blocked items with a reason instead of errors, so one bad group does not hide the rest.
    private static PlanItem ToItem(
        GroupPlan plan, WallPlacementPreview preview, string? problem, Level baseLevel, Level topLevel,
        double baseOffsetMm, double topOffsetMm, string? tagParameterName)
    {
        var group = plan.Group;
        var reasons = new List<string>();
        if (problem is not null) reasons.Add(problem);
        if (group.Sure is { } sure)
        {
            if (!sure) reasons.Add("Bằng chứng chưa đủ, nên xem lại trên mặt bằng.");
            reasons.Add("Vì sao: " + string.Join("; ", group.Walls.SelectMany(wall => wall.Evidence ?? [])
                .GroupBy(reason => reason).OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key} ({g.Count()}/{group.Walls.Count})")));
            var grids = group.Walls.Where(wall => wall.Grid is not null).Select(wall => wall.Grid!).Distinct().ToList();
            if (grids.Count > 0) reasons.Add("Trên trục: " + string.Join(", ", grids));
        }
        else if (group.TagName is null) reasons.Add("Nhóm không có tên đọc từ Text.");
        var repaired = group.Walls.Count(wall => wall.IsCrossLayerPair);
        if (repaired > 0) reasons.Add($"{repaired} vách được ghép từ nét mồ côi, nên kiểm tra.");
        if (preview.FullyCovered > 0) reasons.Add($"{preview.FullyCovered} vách đã có trong model, sẽ bỏ qua.");

        var typeText = plan.Type is not null ? $"Type {plan.Type.Name}"
            : plan.NewTypeName is not null ? $"tạo Type {plan.NewTypeName} từ {plan.Template!.Name}"
            : "chưa có Type";
        var name = group.TagName ?? group.Sure switch { true => "Vách", false => "Vách (xem lại)", null => "(không tên)" };
        return new PlanItem
        {
            ItemId = "W-" + group.Key,
            Action = "create_walls",
            Params = JsonSerializer.SerializeToElement(new
            {
                summary = $"{name} · dày {group.SizeBMm:0} mm · {preview.WallsToPlace} vách ({preview.PiecesToCreate} đoạn) · {typeText}",
                groupKey = group.Key,
                tagName = group.TagName,
                thicknessMm = group.SizeBMm,
                typeId = plan.Type is null ? null : RevitJson.Id(plan.Type.Id),
                newTypeName = plan.NewTypeName,
                templateTypeId = plan.Template is null ? null : RevitJson.Id(plan.Template.Id),
                baseLevelId = RevitJson.Id(baseLevel.Id),
                topLevelId = RevitJson.Id(topLevel.Id),
                baseOffsetMm,
                topOffsetMm,
                tagParameterName,
                // Wall centerlines in model coordinates: [x0, y0, x1, y1] in mm.
                lines = group.Walls.Select(wall => new[]
                {
                    UnitConvert.Length(wall.Start.X), UnitConvert.Length(wall.Start.Y),
                    UnitConvert.Length(wall.End.X), UnitConvert.Length(wall.End.Y),
                }).ToList(),
            }),
            Tier = problem is null ? PlanTier.Review : PlanTier.Blocked,
            Reasons = reasons,
            Source = JsonSerializer.SerializeToElement(new
            {
                name = $"{name} {group.SizeBMm:0}",
                category = "Walls",
                count = group.Walls.Count,
            }),
        };
    }

    private static WallType RequireWallType(Document document, ElementId id, string param) =>
        document.GetElement(id) is WallType { Kind: WallKind.Basic } type
            ? type
            : throw RpcException.InvalidParams(param, "is not a Basic Wall type. Use exactType/nearestType IDs from scan_cad_walls.");

    private static ElementId ParseId(string text, string param) =>
        long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id)
            ? new ElementId(id)
            : throw RpcException.InvalidParams(param, "expected a type ID such as \"12345\".");

    private static double Feet(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
}
