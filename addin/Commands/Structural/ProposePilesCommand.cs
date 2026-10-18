using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands.Common;
using Bimtech.RevitMcp.Commands.Plans;
using Bimtech.RevitMcp.Planning;
using Bimtech.RevitMcp.Protocol;
using Bimtech.RevitMcp.Recognition;
using Bimtech.RevitMcp.Ui;
using Bimtech.StructuralCore.Piles;

namespace Bimtech.RevitMcp.Commands.Structural;

// Plans piles and pile caps from a scan_cad_piles scan: one review item per cap, holding the cap (Foundation Slab on
// capLevelId), its piles (top = cap level − that cap's thickness) and the lean concrete under it. Read-only: it saves a
// plan but never opens a Transaction.
//   - Cap thickness per cap group (key = cap mark such as "Đ1", else size "2000x2000"): capTypes entry → capThicknessMm
//     entry → thickness written in the cap mark → capFloorTypeId for all → otherwise Blocked, for the user to say.
//     A thickness without a type of that thickness creates "BTC-DaiMong_<h>" at commit from capTemplateTypeId.
//   - Pile type per diameter group: types entry → type with that Diameter → new type when createMissingTypes.
//   - Cap edges are squared to the grid and rounded to snapMm (default 5); caps and piles are shifted so their offset
//     from the nearest grid is a multiple of snapMm. Piles further than 300 mm from every cap get one Blocked item.
public sealed class ProposePilesCommand(PlanService plans, ReviewWindowHost review, ModelChangeTracker changes) : IRevitCommand
{
    public string Method => "propose_piles";
    // Saves a plan file but never changes the model.
    public CommandAccess Access => CommandAccess.Read;

    private const int MaxListedOrphans = 50;

    private sealed record CapThickness(FloorType? Type, double? ThicknessMm, string Source);

    private sealed record PileType(FamilySymbol? Symbol, FamilySymbol? Template, string? Problem)
    {
        public FamilySymbol? Family => Symbol ?? Template;
    }

    public object? Execute(CommandContext context)
    {
        var p = context.Params;
        var document = context.RequireDocument();
        if (document.IsFamilyDocument)
            throw new RpcException(RpcErrorCodes.NotWritable, "The active document is a family. Open a project model.");
        var scanId = p.RequireString("scanId", maxLength: 64);
        var scan = context.Sessions.Require<PileScan>(PileScan.Kind, scanId, document, "scanId");
        var capLevel = StructuralJson.RequireLevel(document, p.RequireElementId("capLevelId"), "capLevelId");
        var view = document.GetElement(scan.ViewId) as View
            ?? throw RpcException.InvalidParams("scanId", "the view of this scan no longer exists. Scan again.");

        var slabTypes = PileDefaults.CollectFoundationSlabTypes(document);
        var capKeys = scan.Caps.Select(PileScan.CapKey).Distinct().ToList();
        var capTypes = p.GetStringMap("capTypes", maxCount: 500, maxLength: 32);
        var capThickness = p.GetNumberMap("capThicknessMm", 100, 5000, maxCount: 500);
        StructuralJson.RequireKnownKeys(capTypes.Keys, capKeys, "capTypes");
        StructuralJson.RequireKnownKeys(capThickness.Keys, capKeys, "capThicknessMm");
        var capTypeById = capTypes.ToDictionary(entry => entry.Key, entry => RequireSlabType(document, entry.Value, $"capTypes.{entry.Key}"));
        var capForAll = p.Has("capFloorTypeId") ? RequireSlabType(document, p.GetString("capFloorTypeId", 32)!, "capFloorTypeId") : null;
        var capTemplate = p.Has("capTemplateTypeId")
            ? RequireSlabType(document, p.GetString("capTemplateTypeId", 32)!, "capTemplateTypeId")
            : PileDefaults.FindByName(slabTypes, PileDefaults.CapTypeName) ?? slabTypes.FirstOrDefault(t => t.GetCompoundStructure() is not null);

        var withLot = p.GetBool("withLot", true);
        var lotType = !withLot ? null
            : p.Has("lotFloorTypeId") ? RequireSlabType(document, p.GetString("lotFloorTypeId", 32)!, "lotFloorTypeId")
            : PileDefaults.FindByName(slabTypes, PileDefaults.LotTypeName)
              ?? throw RpcException.InvalidParams("lotFloorTypeId",
                  $"no lean-concrete slab type: pass lotFloorTypeId, set withLot=false, or call load_default_family kind=pile.");
        var lotOffsetMm = p.GetDouble("lotOffsetMm", PileDefaults.LotOffsetMm, 0, 5000);
        var embedmentMm = p.GetDouble("embedmentMm", PileFoundationPlacer.DefaultEmbedmentMm, 0, 5000);
        double? pileLengthMm = p.Has("pileLengthMm") ? p.GetDouble("pileLengthMm", 0, 1000, 150_000) : null;
        var snapMm = p.GetDouble("snapMm", PileBoundaryRegularizer.DefaultSnapMm, 0, 100);
        var join = p.GetBool("join", true);

        var assigned = p.GetStringMap("types", maxCount: 200, maxLength: 32);
        StructuralJson.RequireKnownKeys(assigned.Keys, scan.Groups.Select(group => group.Key).ToList(), "types");
        var total = scan.Groups.Sum(group => group.Circles.Count) + scan.Caps.Count;
        if (total > context.Settings.MaxElementsPerWrite)
            throw RpcException.InvalidParams("scanId",
                $"the scan has {total} piles and caps, above the limit of {context.Settings.MaxElementsPerWrite} per call set in "
                + "BIMTECH MCP Settings. Scan a smaller region, or ask the user to raise the limit.");

        var pileTypes = ResolvePileTypes(document, scan.Groups, assigned, p.GetBool("createMissingTypes", false));
        var gridTheta = PileWorkflow.DetectGridTheta(document, view);
        var snap = PileGridSnapper.PlanSnap(document, view, snapMm);
        var existingCaps = PileFoundationPlacer.ExistingCapBoxes(document, lotType?.Id);
        var existingPiles = new Dictionary<string, List<(PileFoundationPlacer.PlanBox Box, ElementId Id)>>();

        var items = new List<PlanItem>();
        var alreadyModeled = new List<string>();
        var ordered = scan.Caps
            .OrderByDescending(cap => Math.Round(UnitConvert.Length(cap.Boundary.Center.Y) / 100))
            .ThenBy(cap => cap.Boundary.Center.X)
            .ToList();
        foreach (var cap in ordered)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            if (PileFoundationPlacer.FindDuplicateCap(cap.Boundary.Center, cap.Boundary.LengthFeet, cap.Boundary.WidthFeet, existingCaps) is { } existingId)
            {
                alreadyModeled.Add($"{cap.Label} (đài {RevitJson.Id(existingId)})");
                continue;
            }
            var thickness = ResolveThickness(cap, capTypeById, capThickness, capForAll, slabTypes);
            var piles = scan.Groups
                .SelectMany(group => group.Circles.Where(circle => circle.OwningBoundary == cap).Select(circle => (Group: group, Circle: circle)))
                .ToList();
            var item = CapItem(document, items.Count, cap, capLevel, thickness, capTemplate, piles, pileTypes, existingPiles,
                gridTheta, snap, snapMm, lotType, lotOffsetMm, embedmentMm, pileLengthMm, join);
            if (scan.MaybeCaps?.Contains(cap) == true) item.Reasons.Add("Bằng chứng chưa đủ, nên xem lại trên mặt bằng.");
            if (scan.CapEvidence?.GetValueOrDefault(cap) is { Count: > 0 } evidence) item.Reasons.Add("Vì sao: " + string.Join("; ", evidence));
            // As sure as the cap and its least sure pile; a cap type to create needs a look.
            if (scan.CapPercents is { } capPercents && capPercents.TryGetValue(cap, out var capPercent) && item.Tier == PlanTier.Review)
                item.Confidence = ConfidenceRules.Group(
                    piles.Select(pile => scan.PilePercents?.GetValueOrDefault(pile.Circle, Evidence.BasePercent) ?? Evidence.BasePercent).Append(capPercent),
                    needsLook: thickness.Type is null);
            // Named by where it is, so the reviewer finds it on the plan: "Đài #37 · 4900x4500 · B-2".
            var at = scan.CapGrids?.GetValueOrDefault(cap);
            item.Source = JsonSerializer.SerializeToElement(new
            {
                name = $"{cap.Label} · {PileScan.SizeKey(cap)}" + (at is null ? "" : $" · {at}"),
                category = "Structural Foundations",
            });
            items.Add(item);
        }

        var orphans = scan.Groups.SelectMany(group => group.Circles.Where(circle => circle.OwningBoundary is null)).ToList();
        if (orphans.Count > 0) items.Add(OrphanItem(orphans));
        if (items.Count == 0)
            return new
            {
                plan = (object?)null,
                alreadyModeledCaps = alreadyModeled,
                note = "Every cap of this scan already exists in the model. No plan was created.",
            };

        var plan = plans.Create(
            PlanStorageLocation.For(document),
            kind: "create_piles",
            title: $"Dựng {items.Count(item => item.Tier != PlanTier.Blocked)} đài móng và cọc trên {capLevel.Name}",
            scope: JsonSerializer.SerializeToElement(new
            {
                scanId,
                capLevel = StructuralJson.Ref(capLevel),
                snapMm,
                lotType = lotType?.Name,
                alreadyModeledCaps = alreadyModeled,
                pilesWithoutCap = orphans.Count,
            }),
            items: items,
            modelStamp: changes.Current(document));
        review.Show(plan, context.UIApplication);

        return new
        {
            plan = PlanResults.Status(plan),
            caps = items.Count(item => item.ItemId.StartsWith('C')),
            blockedCaps = items.Where(item => item.Tier == PlanTier.Blocked && item.ItemId.StartsWith('C'))
                .Select(item => new { item.ItemId, reason = item.Reasons.FirstOrDefault() }),
            pilesWithoutCap = orphans.Count,
            alreadyModeledCaps = alreadyModeled,
            gridSnapping = snap is null ? "off (no grid in the view or snapMm = 0)" : $"offsets from grids rounded to {snapMm} mm",
            reviewWindow = "opened",
            note = "Nothing was changed in the model. Each item is one cap with its piles and lean concrete. Caps whose "
                + "thickness is unknown are Blocked: ask the user and propose again with capTypes or capThicknessMm. "
                + "Ask the user to review in Revit and say when they are done, then call get_plan_status and commit_plan.",
        };
    }

    private static FloorType RequireSlabType(Document document, string idText, string param)
    {
        if (!long.TryParse(idText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id))
            throw RpcException.InvalidParams(param, "expected a type ID such as \"12345\".");
        return document.GetElement(new ElementId(id)) is FloorType { Category.BuiltInCategory: BuiltInCategory.OST_StructuralFoundation } type
            ? type
            : throw RpcException.InvalidParams(param, "is not a Foundation Slab type. Use an ID from foundationSlabTypes of scan_cad_piles.");
    }

    private static CapThickness ResolveThickness(
        BoundaryRow cap, IReadOnlyDictionary<string, FloorType> capTypes, IReadOnlyDictionary<string, double> capThickness,
        FloorType? capForAll, IReadOnlyList<FloorType> slabTypes)
    {
        var key = PileScan.CapKey(cap);
        if (capTypes.TryGetValue(key, out var type)) return new(type, Thickness(type), $"Type {type.Name} (capTypes)");
        if (capThickness.TryGetValue(key, out var mm)) return FromThickness(mm, "do người dùng nhập (capThicknessMm)");
        if (cap.TagThicknessMm is { } tag) return FromThickness(tag, $"theo ký hiệu bản vẽ \"{cap.TagText}\"");
        if (capForAll is not null) return new(capForAll, Thickness(capForAll), $"Type {capForAll.Name} (capFloorTypeId, chung cho mọi đài)");
        return new(null, null, "");

        CapThickness FromThickness(double thicknessMm, string source) =>
            new(PileDefaults.FindByThickness(slabTypes, thicknessMm), thicknessMm, source);
    }

    private static double? Thickness(FloorType type) =>
        PileFoundationPlacer.CapThicknessFeet(type) is { } feet ? UnitConvert.Length(feet) : null;

    private static Dictionary<PileSizeGroup, PileType> ResolvePileTypes(
        Document document, IReadOnlyList<PileSizeGroup> groups, IReadOnlyDictionary<string, string> assigned, bool createMissing)
    {
        var available = PileTypeMatcher.CollectFoundationTypes(document);
        return groups.ToDictionary(group => group, group =>
        {
            if (assigned.TryGetValue(group.Key, out var typeId))
                return new PileType(StructuralJson.RequireType(document, typeId, $"types.{group.Key}",
                    BuiltInCategory.OST_StructuralFoundation, "scan_cad_piles"), null, null);
            if (PileTypeMatcher.FindExactMatch(group.DiameterMm, available, s => s) is { } exact) return new PileType(exact, null, null);
            var nearest = PileTypeMatcher.AutoMatch(group.DiameterMm, available, s => s);
            return createMissing && nearest is not null
                ? new PileType(null, nearest, null)
                : new PileType(null, null, nearest is null
                    ? $"Chưa có Type cọc nào có tham số Diameter cho {group.Key}: gọi load_default_family kind=pile."
                    : $"Chưa có Type cọc {group.Key}: gán trong 'types' hoặc bật createMissingTypes.");
        });
    }

    private static PlanItem CapItem(
        Document document, int index, BoundaryRow cap, Level capLevel, CapThickness thickness, FloorType? capTemplate,
        List<(PileSizeGroup Group, RawCircle Circle)> piles, IReadOnlyDictionary<PileSizeGroup, PileType> pileTypes,
        Dictionary<string, List<(PileFoundationPlacer.PlanBox Box, ElementId Id)>> existingPiles,
        double? gridTheta, Func<XYZ, XYZ>? snap, double snapMm, FloorType? lotType, double lotOffsetMm, double embedmentMm,
        double? pileLengthMm, bool join)
    {
        var key = PileScan.CapKey(cap);
        var reasons = new List<string>();
        var problems = new List<string>();

        // Squared to the grid, edges rounded to snapMm, then shifted so the centre's offset from the grid is a multiple of snapMm.
        var regularized = PileBoundaryRegularizer.Regularize(cap.Boundary, gridTheta, capLevel.Elevation, snapMm);
        if (regularized.FallbackReason is not null) reasons.Add(regularized.FallbackReason);
        var outline = regularized.Loop?.Select(curve => curve.GetEndPoint(0)).ToList() ?? [];
        if (outline.Count < 3) problems.Add("Không dựng được biên dạng đài.");
        var shift = snap is null || outline.Count == 0
            ? XYZ.Zero
            : snap(new XYZ(outline.Average(point => point.X), outline.Average(point => point.Y), 0));

        if (thickness.ThicknessMm is null)
            problems.Add($"Chưa biết chiều dày đài {key}: bản vẽ không ghi. Hỏi người dùng rồi propose lại với capTypes hoặc capThicknessMm.");
        else
        {
            reasons.Add($"Dày {thickness.ThicknessMm:0} mm, {thickness.Source}.");
            if (thickness.Type is null && capTemplate is null)
                problems.Add("Model chưa có Type Foundation Slab nào để tạo Type đài đúng chiều dày.");
        }

        var pileEntries = new List<object>();
        var existingCount = 0;
        foreach (var (group, circle) in piles)
        {
            var type = pileTypes[group];
            if (type.Problem is not null)
            {
                if (!problems.Contains(type.Problem)) problems.Add(type.Problem);
                continue;
            }
            var familyName = type.Family!.Family.Name;
            if (!existingPiles.TryGetValue(familyName, out var boxes))
                existingPiles[familyName] = boxes = PileFoundationPlacer.ExistingPileBoxes(document, familyName);
            if (PileFoundationPlacer.FindDuplicate(circle.Center, boxes) is not null)
            {
                existingCount++;
                continue;
            }
            var center = circle.Center + (snap?.Invoke(circle.Center) ?? XYZ.Zero);
            pileEntries.Add(new
            {
                x = UnitConvert.Length(center.X),
                y = UnitConvert.Length(center.Y),
                diameterMm = group.DiameterMm,
                typeId = type.Symbol is null ? null : RevitJson.Id(type.Symbol.Id),
                templateTypeId = type.Template is null ? null : RevitJson.Id(type.Template.Id),
            });
        }
        if (existingCount > 0) reasons.Add($"{existingCount} cọc đã có trong model, bỏ qua.");
        if (piles.Count == 0) reasons.Add("Đài không có cọc nào trong bản vẽ.");

        var diameters = string.Join(", ", piles.GroupBy(pile => pile.Group.Key).Select(g => $"{g.Count()}×{g.Key}"));
        var typeText = thickness.Type is not null ? thickness.Type.Name
            : thickness.ThicknessMm is { } mm ? $"tạo BTC-DaiMong_{mm:0}" : "chưa biết chiều dày";
        var size = PileScan.SizeKey(cap);
        return new PlanItem
        {
            ItemId = "C" + (index + 1).ToString(CultureInfo.InvariantCulture),
            Action = "create_pile_cap",
            Params = JsonSerializer.SerializeToElement(new
            {
                summary = $"Đài {(cap.TagName is null ? "" : cap.TagName + " ")}{size} · {typeText} · "
                    + (piles.Count == 0 ? "không cọc" : $"cọc {diameters}") + (lotType is null ? "" : " · lót"),
                capKey = key,
                capLevelId = RevitJson.Id(capLevel.Id),
                // Outline in model coordinates: [x, y] in mm, in order, at the cap level.
                outline = outline.Select(point => new[] { UnitConvert.Length(point.X + shift.X), UnitConvert.Length(point.Y + shift.Y) }).ToList(),
                capTypeId = thickness.Type is null ? null : RevitJson.Id(thickness.Type.Id),
                capThicknessMm = thickness.ThicknessMm,
                capTemplateTypeId = thickness.Type is null && capTemplate is not null ? RevitJson.Id(capTemplate.Id) : null,
                lotTypeId = lotType is null ? null : RevitJson.Id(lotType.Id),
                lotOffsetMm,
                embedmentMm,
                pileLengthMm,
                join,
                piles = pileEntries,
            }),
            Tier = problems.Count == 0 ? PlanTier.Review : PlanTier.Blocked,
            Reasons = [.. problems, .. reasons],
            Source = JsonSerializer.SerializeToElement(new { name = cap.Label, category = "Structural Foundations" }),
        };
    }

    // Piles in no cap have no elevation, so they are shown, never placed.
    private static PlanItem OrphanItem(List<RawCircle> orphans) => new()
    {
        ItemId = "P-NOCAP",
        Action = "create_pile_cap",
        Params = JsonSerializer.SerializeToElement(new
        {
            summary = $"{orphans.Count} cọc không thuộc đài nào (cách mọi đài hơn {PileOwnershipResolver.OwnerToleranceMm:0} mm)",
            piles = orphans.Take(MaxListedOrphans).Select(circle => new
            {
                x = UnitConvert.Length(circle.Center.X),
                y = UnitConvert.Length(circle.Center.Y),
                diameterMm = DiameterMm(circle),
            }),
        }),
        Tier = PlanTier.Blocked,
        Reasons =
        [
            "Không biết cao độ đỉnh của các cọc này vì chúng không nằm trong đài nào. Có thể là vòng tròn lạc trên layer cọc "
                + "(lỗ kỹ thuật, ký hiệu) hoặc cọc dưới móng băng/vách. Kiểm tra bản vẽ; nếu là cọc thật thì cần thêm đài.",
        ],
        Source = JsonSerializer.SerializeToElement(new { name = "Cọc ngoài đài", category = "Structural Foundations" }),
    };

    private static double DiameterMm(RawCircle circle) => Math.Round(UnitConvert.Length(circle.RadiusFeet * 2));
}
