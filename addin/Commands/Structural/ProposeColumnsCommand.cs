using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands.Common;
using Bimtech.RevitMcp.Commands.Plans;
using Bimtech.RevitMcp.Planning;
using Bimtech.RevitMcp.Protocol;
using Bimtech.RevitMcp.Recognition;
using Bimtech.RevitMcp.Ui;
using Bimtech.StructuralCore.Columns;
using Bimtech.StructuralCore.Piles;

namespace Bimtech.RevitMcp.Commands.Structural;

// Plans structural columns from a scan_cad_columns scan between two levels: one review item per group (mark and size).
// Read-only: it saves a plan but never opens a Transaction.
//   - Type per group: "types" entry → existing type with matching b/h → type in templateFamilyId (reused or created).
//   - Columns usually stand at grid crossings (GridIntersections): per direction, an offset within gridToleranceMm of
//     centred or face-on-grid is set exactly, any other offset is rounded to snapMm (default 5). Items list the
//     crossings ("A-1"); columns on one grid only or on none are flagged, and crossings with no column are returned.
//   - A column drawn within 2° of the grid direction is set straight on it; columns drawn twice are placed once.
//   - Columns already in the model (any family, overlapping heights) are skipped.
public sealed class ProposeColumnsCommand(PlanService plans, ReviewWindowHost review, ModelChangeTracker changes) : IRevitCommand
{
    public string Method => "propose_columns";
    // Saves a plan file but never changes the model.
    public CommandAccess Access => CommandAccess.Read;

    private const double AngleSnapDegrees = 2.0;
    private const double SameColumnMm = 20.0;
    private const int MaxListed = 8;
    private const int MaxListedCrossings = 50;

    private sealed record TypeChoice(FamilySymbol? Symbol, Family? TemplateFamily, string Text, string? Problem);

    public object? Execute(CommandContext context)
    {
        var p = context.Params;
        var document = context.RequireDocument();
        if (document.IsFamilyDocument)
            throw new RpcException(RpcErrorCodes.NotWritable, "The active document is a family. Open a project model.");
        var scanId = p.RequireString("scanId", maxLength: 64);
        var scan = context.Sessions.Require<ColumnScan>(ColumnScan.Kind, scanId, document, "scanId");
        var baseLevel = StructuralJson.RequireLevel(document, p.RequireElementId("baseLevelId"), "baseLevelId");
        var topLevel = StructuralJson.RequireLevel(document, p.RequireElementId("topLevelId"), "topLevelId");
        if (topLevel.Elevation <= baseLevel.Elevation)
            throw RpcException.InvalidParams("topLevelId", "must be higher than baseLevelId.");
        var view = document.GetElement(scan.ViewId) as View
            ?? throw RpcException.InvalidParams("scanId", "the view of this scan no longer exists. Scan again.");

        var total = scan.Groups.Sum(group => group.Columns.Count);
        if (total > context.Settings.MaxElementsPerWrite)
            throw RpcException.InvalidParams("scanId",
                $"the scan has {total} columns, above the limit of {context.Settings.MaxElementsPerWrite} per call set in "
                + "BIMTECH MCP Settings. Scan a smaller region, or ask the user to raise the limit.");
        var assigned = p.GetStringMap("types", maxCount: 200, maxLength: 32);
        StructuralJson.RequireKnownKeys(assigned.Keys, scan.Groups.Select(group => group.Key).ToList(), "types");
        var templateFamily = p.Has("templateFamilyId")
            ? StructuralJson.RequireFamily(document, p.RequireElementId("templateFamilyId"), BuiltInCategory.OST_StructuralColumns)
            : null;
        var snapMm = p.GetDouble("snapMm", PileBoundaryRegularizer.DefaultSnapMm, 0, 100);
        var toleranceMm = p.GetDouble("gridToleranceMm", GridIntersections.DefaultToleranceMm, 0, 100);
        var marginMm = p.GetDouble("gridMarginMm", GridIntersections.DefaultMarginMm, 0, 5000);
        var writeMark = p.GetBool("writeMark", true);

        var gridTheta = PileWorkflow.DetectGridTheta(document, view);
        var frame = PileGridSnapper.PlanGridFrame(document, view);
        var existing = new ColumnPlacer.DuplicateChecker(document, baseLevel, topLevel);
        var available = ColumnTypeMatcher.CollectColumnTypes(document);
        var placed = new List<XYZ>();
        // Every column of the scan, in the grid frame, to find the crossings no column reaches.
        var footprints = new List<GridIntersections.Column>();

        var items = new List<PlanItem>();
        var alreadyModeled = new List<string>();
        foreach (var group in scan.Groups)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            var type = ChooseType(document, group, assigned, available, templateFamily);
            var reasons = new List<string>();
            if (type.Problem is not null) reasons.Add(type.Problem);
            if (scan.TagMismatches.TryGetValue(group.Key, out var mismatch)) reasons.Add(mismatch);

            var columns = new List<object>();
            int skipped = 0, doubled = 0, straightened = 0, setOnGrid = 0;
            var crossings = new List<string>();
            var oneGrid = new List<string>();
            var offGrid = new List<string>();
            foreach (var column in group.Columns)
            {
                if (placed.Any(other => other.DistanceTo(column.Center) <= Feet(SameColumnMm)))
                {
                    doubled++;
                    continue;
                }
                placed.Add(column.Center);

                var center = column.Center;
                var angle = column.AngleRadians;
                if (gridTheta is { } theta)
                {
                    // The grid direction nearest the drawn one: theta + k·90°, so a column drawn at 89° with grids at 0°
                    // becomes 90°, not 0° (which would swap b and h).
                    var drawn = angle * 180 / Math.PI;
                    var straight = theta + Math.Round((drawn - theta) / 90) * 90;
                    if (Math.Abs(drawn - straight) is > 1e-6 and <= AngleSnapDegrees)
                    {
                        angle = straight * Math.PI / 180;
                        straightened++;
                    }
                }
                // Where the column stands among the grids: at a crossing, on one grid, or on none. Offsets within
                // gridToleranceMm of centred or face-on-grid are set exactly; others are rounded to snapMm.
                string? at = null;
                var isExisting = existing.Find(column.Center) is not null;
                if (frame is not null)
                {
                    var (lengthMm, widthMm) = (UnitConvert.Length(column.LengthFeet), UnitConvert.Length(column.WidthFeet));
                    var fromU = angle - Math.Atan2(frame.UAxis.Y, frame.UAxis.X);
                    var (halfU, halfV) = (GridIntersections.HalfExtent(lengthMm, widthMm, fromU),
                        GridIntersections.HalfExtent(lengthMm, widthMm, fromU - Math.PI / 2));
                    var (u, v) = (UnitConvert.Length(center.DotProduct(frame.UAxis)), UnitConvert.Length(center.DotProduct(frame.VAxis)));
                    var fitU = GridIntersections.Fit(u, halfU, frame.AlongV, snapMm, marginMm, toleranceMm);
                    var fitV = GridIntersections.Fit(v, halfV, frame.AlongU, snapMm, marginMm, toleranceMm);
                    center += frame.UAxis * Feet(fitU.ShiftMm) + frame.VAxis * Feet(fitV.ShiftMm);
                    footprints.Add(new GridIntersections.Column(u + fitU.ShiftMm, v + fitV.ShiftMm, halfU, halfV));
                    at = (fitU.OnGrid, fitV.OnGrid) switch
                    {
                        (true, true) => GridIntersections.Label(fitU.GridName!, fitV.GridName!),
                        (true, false) => $"trục {fitU.GridName}",
                        (false, true) => $"trục {fitV.GridName}",
                        _ => null,
                    };
                    if (!isExisting)
                    {
                        if (IsSetOnGrid(fitU) || IsSetOnGrid(fitV)) setOnGrid++;
                        var where = at ?? $"({UnitConvert.Length(center.X):0}, {UnitConvert.Length(center.Y):0})";
                        if (fitU.OnGrid && fitV.OnGrid) crossings.Add(where);
                        else if (at is not null) oneGrid.Add(where);
                        else offGrid.Add(where);
                    }
                }

                if (isExisting)
                {
                    skipped++;
                    continue;
                }
                columns.Add(new
                {
                    x = UnitConvert.Length(center.X),
                    y = UnitConvert.Length(center.Y),
                    angleDeg = Math.Round(angle * 180 / Math.PI, 4),
                    lengthMm = UnitConvert.Length(column.LengthFeet),
                    widthMm = UnitConvert.Length(column.WidthFeet),
                    at,
                });
            }

            if (columns.Count == 0 && type.Problem is null)
            {
                alreadyModeled.Add(group.Key);
                continue;
            }
            if (skipped > 0) reasons.Add($"{skipped} cột đã có trong model (mọi family), bỏ qua.");
            if (doubled > 0) reasons.Add($"{doubled} cột vẽ trùng (cùng tâm), chỉ đặt 1 lần.");
            if (straightened > 0) reasons.Add($"{straightened} cột vẽ lệch dưới {AngleSnapDegrees:0}° so với phương trục: đặt thẳng theo trục.");
            if (setOnGrid > 0)
                reasons.Add($"{setOnGrid} cột vẽ lệch tối đa {toleranceMm:0} mm so với tâm hoặc mép trùng trục: đặt đúng vào trục.");
            if (oneGrid.Count > 0)
                reasons.Add($"{oneGrid.Count} cột chỉ nằm trên 1 trục, không ở giao trục (cột phụ, cột thang?): {List(oneGrid)}");
            if (offGrid.Count > 0)
                reasons.Add($"{offGrid.Count} cột không nằm trên trục nào, kiểm tra: {List(offGrid)}");

            var place = crossings.Count == 0 ? "" : $" · tại {List(crossings)}";
            items.Add(new PlanItem
            {
                ItemId = "K-" + group.Key.Replace(' ', '_'),
                Action = "create_columns",
                Params = JsonSerializer.SerializeToElement(new
                {
                    summary = $"{(group.TagName is null ? "" : group.TagName + " · ")}{group.SizeKey} · {columns.Count} cột · {type.Text}{place}",
                    groupKey = group.Key,
                    mark = writeMark ? group.TagName : null,
                    baseLevelId = RevitJson.Id(baseLevel.Id),
                    topLevelId = RevitJson.Id(topLevel.Id),
                    typeId = type.Symbol is null ? null : RevitJson.Id(type.Symbol.Id),
                    templateFamilyId = type.Symbol is null && type.TemplateFamily is not null ? RevitJson.Id(type.TemplateFamily.Id) : null,
                    sizeAMm = group.SizeAMm,
                    sizeBMm = group.SizeBMm,
                    // Centres in model coordinates (mm), angle of the drawn Length side from X (degrees).
                    columns,
                }),
                Tier = type.Problem is null ? PlanTier.Review : PlanTier.Blocked,
                Reasons = reasons,
                Source = JsonSerializer.SerializeToElement(new { name = group.Key, category = "Structural Columns", count = columns.Count }),
            });
        }

        var emptyCrossings = frame is null ? [] : GridIntersections.Empty(frame.AlongU, frame.AlongV, footprints, marginMm);
        var emptyCrossingsInfo = new { count = emptyCrossings.Count, names = emptyCrossings.Take(MaxListedCrossings).ToList() };
        if (items.Count == 0)
            return new
            {
                plan = (object?)null,
                alreadyModeledGroups = alreadyModeled,
                emptyCrossings = emptyCrossingsInfo,
                note = "Every column of this scan already exists in the model. No plan was created.",
            };

        var plan = plans.Create(
            PlanStorageLocation.For(document),
            kind: "create_columns",
            title: $"Dựng cột {baseLevel.Name} → {topLevel.Name}, {items.Count} nhóm",
            scope: JsonSerializer.SerializeToElement(new
            {
                scanId,
                baseLevel = StructuralJson.Ref(baseLevel),
                topLevel = StructuralJson.Ref(topLevel),
                snapMm,
                gridToleranceMm = toleranceMm,
                gridMarginMm = marginMm,
                alreadyModeledGroups = alreadyModeled,
                emptyCrossings = emptyCrossingsInfo,
            }),
            items: items,
            modelStamp: changes.Current(document));
        review.Show(plan, context.UIApplication);

        return new
        {
            plan = PlanResults.Status(plan),
            groups = items.Count,
            blockedGroups = items.Where(item => item.Tier == PlanTier.Blocked).Select(item => new { item.ItemId, reason = item.Reasons.FirstOrDefault() }),
            alreadyModeledGroups = alreadyModeled,
            gridSnapping = frame is null ? "off (no grid in the view)"
                : snapMm <= 0 ? "off (snapMm = 0): positions as drawn"
                : $"centred or face-on-grid within {toleranceMm} mm set exactly, other offsets rounded to {snapMm} mm",
            emptyCrossings = emptyCrossingsInfo,
            reviewWindow = "opened",
            note = "Nothing was changed in the model. Each item is one column group. "
                + (emptyCrossings.Count > 0
                    ? $"Tell the user the {emptyCrossings.Count} grid crossing(s) with no column (open space on purpose, or a "
                      + "column on another layer). "
                    : "")
                + "Ask the user to review in Revit and say when they are done, then call get_plan_status and commit_plan.",
        };
    }

    // The offset was moved onto the grid (centred or face on it), not just rounded.
    private static bool IsSetOnGrid(GridIntersections.AxisFit fit) =>
        fit.Placement is GridIntersections.Placement.Centered or GridIntersections.Placement.FaceOnGrid && Math.Abs(fit.ShiftMm) >= 0.5;

    private static string List(List<string> names) =>
        string.Join(", ", names.Take(MaxListed)) + (names.Count > MaxListed ? "..." : "");

    private static TypeChoice ChooseType(
        Document document, ColumnSizeGroup group, IReadOnlyDictionary<string, string> assigned,
        IReadOnlyList<FamilySymbol> available, Family? templateFamily)
    {
        if (assigned.TryGetValue(group.Key, out var typeId))
        {
            var symbol = StructuralJson.RequireType(document, typeId, $"types.{group.Key}", BuiltInCategory.OST_StructuralColumns, "scan_cad_columns");
            return new TypeChoice(symbol, null, $"Type {symbol.Name}", null);
        }
        if (ColumnTypeMatcher.FindExactMatch(group.SizeAMm, group.SizeBMm, available, s => s) is { } exact)
            return new TypeChoice(exact, null, $"Type {exact.Name} ({exact.FamilyName})", null);
        if (templateFamily is not null)
            return new TypeChoice(null, templateFamily, $"tạo Type {group.SizeKey} trong {templateFamily.Name}", null);
        return new TypeChoice(null, null, "chưa có Type",
            $"Chưa có Type cột {group.SizeKey}: gán trong 'types' hoặc truyền templateFamilyId (load_default_family kind=column).");
    }

    private static double Feet(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
}
