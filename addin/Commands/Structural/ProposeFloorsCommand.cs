using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands.Common;
using Bimtech.RevitMcp.Commands.Plans;
using Bimtech.RevitMcp.Planning;
using Bimtech.RevitMcp.Protocol;
using Bimtech.RevitMcp.Ui;
using Bimtech.StructuralCore.Floors;

namespace Bimtech.RevitMcp.Commands.Structural;

// Plans structural floors on a level from the model, not from CAD: the centerlines of beams on the level and of
// straight walls crossing it, as shown in a plan view, are traced into closed panels (FloorLoopFinder.FromModel);
// panels under 1 m² count as openings. One review item per panel. Read-only: it saves a plan, never a Transaction.
// Floor type: floorTypeId → BTC_SanKC_150 → BTC_SanKC_150 created at commit time from a floor type with layers.
public sealed class ProposeFloorsCommand(PlanService plans, ReviewWindowHost review, ModelChangeTracker changes) : IRevitCommand
{
    public string Method => "propose_floors";
    // Saves a plan file but never changes the model.
    public CommandAccess Access => CommandAccess.Read;

    public object? Execute(CommandContext context)
    {
        var p = context.Params;
        var document = context.RequireDocument();
        if (document.IsFamilyDocument)
            throw new RpcException(RpcErrorCodes.NotWritable, "The active document is a family. Open a project model.");
        var level = StructuralJson.RequireLevel(document, p.RequireElementId("levelId"), "levelId");
        var view = RequireLevelView(context, document, level);
        var offsetMm = p.GetDouble("offsetMm", 0, -100_000, 100_000);

        var floorType = p.Has("floorTypeId")
            ? document.GetElement(p.RequireElementId("floorTypeId")) as FloorType
                ?? throw RpcException.InvalidParams("floorTypeId", "is not a floor type.")
            : FloorDefaults.FindDefaultFloorType(document);
        string? typeProblem = null;
        if (floorType is null && FloorDefaults.FindSeedType(document) is null)
            typeProblem = "Model chưa có Floor Type có cấu tạo lớp để tạo BTC_SanKC_150; truyền floorTypeId.";
        var typeText = floorType?.Name ?? $"tạo Type {FloorDefaults.DefaultFloorTypeName}";

        var scan = FloorLoopFinder.FromModel(document, view, level);
        context.Cancellation.ThrowIfCancellationRequested();
        var counts = new
        {
            centerlines = scan.CenterlineCount,
            closedPanels = scan.TotalLoops,
            skippedAsOpenings = scan.SkippedAsOpening,
        };
        if (scan.ValidLoops.Count == 0)
            return new
            {
                plan = (object?)null,
                found = counts,
                note = scan.CenterlineCount == 0
                    ? "No beam or straight wall centerlines on this level in this view. Place beams (Reference Level = this level) or walls first."
                    : "The centerlines form no closed panel of 1 m² or more. Check that beams meet at their ends.",
            };
        var limit = context.Settings.MaxElementsPerWrite;
        if (scan.ValidLoops.Count > limit)
            throw RpcException.InvalidParams("levelId",
                $"{scan.ValidLoops.Count} panels found, above the limit of {limit} per call set in BIMTECH MCP Settings.");

        var existingFloors = new FilteredElementCollector(document).OfClass(typeof(Floor)).Cast<Floor>()
            .Where(floor => floor.LevelId == level.Id)
            .Select(floor => (floor.Id, Box: floor.get_BoundingBox(null)))
            .Where(entry => entry.Box is not null)
            .ToList();

        // Panels read top to bottom, left to right, so S1 is the top-left panel as on a drawing.
        var panels = scan.ValidLoops
            .Select(loop => PolygonMath.Tessellate(loop))
            .Select(points => points.Count > 1 && points[^1].DistanceTo(points[0]) < 1e-6 ? points[..^1] : points)
            .Select(points => (Points: points, Center: PolygonMath.Centroid(points)))
            .OrderByDescending(panel => Math.Round(UnitConvert.Length(panel.Center.Y) / 100))
            .ThenBy(panel => panel.Center.X)
            .ToList();
        var items = panels.Select((panel, index) =>
        {
            var reasons = new List<string>();
            if (typeProblem is not null) reasons.Add(typeProblem);
            var overlapping = existingFloors.FirstOrDefault(entry =>
                panel.Center.X >= entry.Box.Min.X && panel.Center.X <= entry.Box.Max.X
                && panel.Center.Y >= entry.Box.Min.Y && panel.Center.Y <= entry.Box.Max.Y);
            if (overlapping.Box is not null)
                reasons.Add($"Có thể trùng sàn {RevitJson.Id(overlapping.Id)} đã có; khi ghi, sàn chồng lên sàn cũ sẽ tự bỏ qua.");

            var areaM2 = UnitUtils.ConvertFromInternalUnits(PolygonMath.AreaAbs(panel.Points), UnitTypeId.SquareMeters);
            var label = "S" + (index + 1).ToString(CultureInfo.InvariantCulture);
            return new PlanItem
            {
                ItemId = label,
                Action = "create_floor",
                Params = JsonSerializer.SerializeToElement(new
                {
                    summary = $"Ô sàn {label} · {areaM2.ToString("0.##", CultureInfo.InvariantCulture)} m² · "
                        + $"{panel.Points.Count} đỉnh · {typeText}",
                    levelId = RevitJson.Id(level.Id),
                    offsetMm,
                    floorTypeId = floorType is null ? null : RevitJson.Id(floorType.Id),
                    areaM2 = Math.Round(areaM2, 3),
                    // Outline in model coordinates: [x, y] in mm, in order, not repeating the first point.
                    points = panel.Points.Select(point => new[] { UnitConvert.Length(point.X), UnitConvert.Length(point.Y) }).ToList(),
                }),
                Tier = typeProblem is null ? PlanTier.Review : PlanTier.Blocked,
                Reasons = reasons,
                Source = JsonSerializer.SerializeToElement(new { name = label, category = "Floors" }),
            };
        }).ToList();

        var plan = plans.Create(
            PlanStorageLocation.For(document),
            kind: "create_floors",
            title: $"Dựng {items.Count} ô sàn trên {level.Name}",
            scope: JsonSerializer.SerializeToElement(new
            {
                level = StructuralJson.Ref(level),
                view = StructuralJson.Ref(view),
                offsetMm,
                floorType = floorType is null ? FloorDefaults.DefaultFloorTypeName + " (tạo mới)" : floorType.Name,
                found = counts,
            }),
            items: items,
            modelStamp: changes.Current(document));
        review.Show(plan, context.UIApplication);

        return new
        {
            plan = PlanResults.Status(plan),
            found = counts,
            floorType = floorType is null ? null : StructuralJson.SlabType(floorType),
            createsDefaultType = floorType is null && typeProblem is null,
            reviewWindow = "opened",
            note = "Nothing was changed in the model. Each item is one floor panel. Ask the user to review in Revit and say "
                + "when they are done, then call get_plan_status and commit_plan.",
        };
    }

    // viewId; else the active view if it is a plan of this level; else the first plan view of this level.
    // Walls are collected as shown in this view.
    private static ViewPlan RequireLevelView(CommandContext context, Document document, Level level)
    {
        if (context.Params.Has("viewId"))
            return document.GetElement(context.Params.RequireElementId("viewId")) as ViewPlan
                ?? throw RpcException.InvalidParams("viewId", "must be a plan view.");
        if (document.ActiveView is ViewPlan active && active.GenLevel?.Id == level.Id)
            return active;
        return new FilteredElementCollector(document).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(plan => !plan.IsTemplate && plan.GenLevel?.Id == level.Id)
                .OrderByDescending(plan => plan.ViewType == ViewType.EngineeringPlan)
                .FirstOrDefault()
            ?? throw RpcException.InvalidParams("viewId", "this level has no plan view. Create one or pass viewId.");
    }
}
