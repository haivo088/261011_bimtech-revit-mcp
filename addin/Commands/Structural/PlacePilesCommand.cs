using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands.Common;
using Bimtech.RevitMcp.Protocol;
using Bimtech.StructuralCore.Piles;

namespace Bimtech.RevitMcp.Commands.Structural;

// Places piles and pile caps of a scan, then — exactly like the WPF tool's "Đặt" button — lean concrete under
// each cap, rounding of dimensions to the grid, and joins between piles and caps (PileWorkflow).
//   dryRun (default true) reports the plan; dryRun false runs in one WriteSession and returns a batchId.
// Cap = Foundation Slab of capFloorTypeId (default BTC-DaiMong_1500) on capLevelId; pile top = cap level − cap thickness.
// Pile type per group: "types" entry → existing type with matching Diameter → new type duplicated from the nearest one
// when createMissingTypes is true → unresolved.
public sealed class PlacePilesCommand : IRevitCommand
{
    public string Method => "place_piles";
    public CommandAccess Access => CommandAccess.Write;

    // A dry run only reads, so previews stay available in read-only mode.
    public bool ModifiesModel(CommandParams parameters) => !parameters.GetBool("dryRun", true);

    private enum TypeSource { Assigned, Exact, CreateType }

    private sealed record GroupPlan(
        PileSizeGroup Group, FamilySymbol? Symbol, TypeSource? Source, FamilySymbol? Template, string? NewTypeName, string? Problem);

    public object? Execute(CommandContext context)
    {
        var p = context.Params;
        var dryRun = p.GetBool("dryRun", true);
        var document = dryRun ? context.RequireDocument() : context.RequireWritableDocument();
        var scanId = p.RequireString("scanId", maxLength: 64);
        var scan = context.Sessions.Require<PileScan>(PileScan.Kind, scanId, document, "scanId");
        var capLevel = StructuralJson.RequireLevel(document, p.RequireElementId("capLevelId"), "capLevelId");
        var view = document.GetElement(scan.ViewId) as View;

        var slabTypes = PileDefaults.CollectFoundationSlabTypes(document);
        var capType = ResolveSlabType(document, p, "capFloorTypeId", PileDefaults.CapTypeName, slabTypes)
            ?? throw RpcException.InvalidParams("capFloorTypeId",
                $"no cap slab type: pass capFloorTypeId, or call load_default_family kind=pile to create {PileDefaults.CapTypeName}.");
        var withLot = p.GetBool("withLot", true);
        var lotType = withLot
            ? ResolveSlabType(document, p, "lotFloorTypeId", PileDefaults.LotTypeName, slabTypes)
              ?? throw RpcException.InvalidParams("lotFloorTypeId",
                  $"no lean-concrete slab type: pass lotFloorTypeId, set withLot=false, or call load_default_family kind=pile.")
            : null;
        var embedmentMm = p.GetDouble("embedmentMm", PileFoundationPlacer.DefaultEmbedmentMm, 0, 5000);
        var lotOffsetMm = p.GetDouble("lotOffsetMm", PileDefaults.LotOffsetMm, 0, 5000);

        var assigned = p.GetStringMap("types", maxCount: 200, maxLength: 32);
        StructuralJson.RequireKnownKeys(assigned.Keys, scan.Groups.Select(group => group.Key).ToList(), "types");

        var total = scan.Groups.Sum(group => group.Circles.Count) + scan.Caps.Count;
        var limit = context.Settings.MaxElementsPerWrite;
        if (total > limit)
            throw RpcException.InvalidParams("scanId",
                $"the scan has {total} piles and caps, above the limit of {limit} per call set in BIMTECH MCP Settings. "
                + "Scan a smaller region, or ask the user to raise the limit.");

        var plans = Resolve(document, scan.Groups, assigned, p.GetBool("createMissingTypes", false));
        var unresolved = plans.Where(plan => plan.Source is null).ToList();
        if (!dryRun && unresolved.Count > 0)
            throw RpcException.InvalidParams("types",
                $"no pile type for group(s) {string.Join(", ", unresolved.Select(plan => plan.Group.Key))}. "
                + "Assign a type in 'types', or set createMissingTypes=true. Use dryRun=true to review.");

        return dryRun
            ? Preview(document, view, scanId, scan, plans, capLevel, capType, lotType)
            : Run(context, document, view, scan, plans, capLevel, capType, embedmentMm, lotType, lotOffsetMm);
    }

    private static FloorType? ResolveSlabType(
        Document document, CommandParams p, string param, string defaultName, IReadOnlyList<FloorType> slabTypes)
    {
        if (!p.Has(param)) return PileDefaults.FindByName(slabTypes, defaultName);
        return document.GetElement(p.RequireElementId(param)) is FloorType type && type.Category?.BuiltInCategory == BuiltInCategory.OST_StructuralFoundation
            ? type
            : throw RpcException.InvalidParams(param, "is not a Foundation Slab type. Use an ID from foundationSlabTypes of scan_cad_piles.");
    }

    private static List<GroupPlan> Resolve(
        Document document, IReadOnlyList<PileSizeGroup> groups, IReadOnlyDictionary<string, string> assigned, bool createMissingTypes)
    {
        var available = PileTypeMatcher.CollectFoundationTypes(document);
        var plans = new List<GroupPlan>();
        var toCreate = new List<(PileSizeGroup Group, FamilySymbol? Template)>();
        foreach (var group in groups)
        {
            if (assigned.TryGetValue(group.Key, out var typeId))
            {
                plans.Add(new GroupPlan(group, StructuralJson.RequireType(document, typeId, $"types.{group.Key}",
                    BuiltInCategory.OST_StructuralFoundation, "scan_cad_piles"), TypeSource.Assigned, null, null, null));
                continue;
            }
            var exact = PileTypeMatcher.FindExactMatch(group.DiameterMm, available, s => s);
            var nearest = PileTypeMatcher.AutoMatch(group.DiameterMm, available, s => s);
            if (exact is not null)
                plans.Add(new GroupPlan(group, exact, TypeSource.Exact, null, null, null));
            else if (createMissingTypes)
                toCreate.Add((group, nearest));
            else
                plans.Add(new GroupPlan(group, null, null, nearest, null,
                    "no type with matching Diameter: assign one in 'types' or set createMissingTypes=true"));
        }

        foreach (var (group, newTypeName, error) in PileFoundationPlacer.PlanMissingTypes(document, toCreate))
        {
            var template = toCreate.First(entry => entry.Group == group).Template;
            plans.Add(newTypeName is not null
                ? new GroupPlan(group, null, TypeSource.CreateType, template, newTypeName, null)
                : new GroupPlan(group, null, null, template, null, error));
        }

        var order = groups.Select((group, index) => (group, index)).ToDictionary(entry => entry.group, entry => entry.index);
        return plans.OrderBy(plan => order[plan.Group]).ToList();
    }

    private static object Preview(
        Document document, View? view, string scanId, PileScan scan, List<GroupPlan> plans,
        Level capLevel, FloorType capType, FloorType? lotType)
    {
        // Duplicates of piles are found per family; a type still to be created belongs to its template's family.
        var assignments = plans
            .Select(plan => new PileAssignment(plan.Group, plan.Symbol ?? (plan.Source == TypeSource.CreateType ? plan.Template : null)))
            .ToList();
        var preview = PileFoundationPlacer.PlanPlacement(
            document, assignments, scan.Caps, capLevel, capType, PileWorkflow.DetectGridTheta(document, view));

        return new
        {
            dryRun = true,
            scanId,
            capLevel = StructuralJson.Ref(capLevel),
            capType = StructuralJson.SlabType(capType),
            lotType = lotType is null ? null : StructuralJson.SlabType(lotType),
            totals = new
            {
                pilesToCreate = preview.Groups.Sum(group => group.ToCreate),
                pileDuplicates = preview.Groups.Sum(group => group.Duplicates),
                pilesWithoutCap = preview.Groups.Sum(group => group.NoCap),
                capsToCreate = preview.CapsToCreate,
                capDuplicates = preview.CapsDuplicates,
                capsFailed = preview.CapsFailed,
                newPileTypes = plans.Count(plan => plan.Source == TypeSource.CreateType),
                unresolvedGroups = plans.Count(plan => plan.Source is null),
            },
            groups = plans.Zip(preview.Groups, (plan, place) => new
            {
                key = plan.Group.Key,
                count = plan.Group.Circles.Count,
                type = plan.Symbol is null ? null : StructuralJson.PileType(plan.Symbol),
                typeAction = plan.Source?.ToString(),
                newTypeName = plan.NewTypeName,
                templateType = plan.Template is null ? null : StructuralJson.PileType(plan.Template),
                toCreate = place.ToCreate,
                duplicates = place.Duplicates,
                withoutCap = place.NoCap,
                problem = plan.Problem ?? place.Problem,
            }).ToList(),
            afterPlacing = new[]
            {
                lotType is null ? "no lean concrete (withLot=false)" : $"lean concrete {lotType.Name} under every cap",
                "round dimensions to the nearest grid",
                "join piles with caps",
            },
            next = plans.Any(plan => plan.Source is null)
                ? "Resolve the groups with a problem (types or createMissingTypes), then preview again."
                : "Show this plan to the user. If they confirm, call place_piles again with the same arguments and dryRun=false.",
        };
    }

    private static object Run(
        CommandContext context, Document document, View? view, PileScan scan, List<GroupPlan> plans,
        Level capLevel, FloorType capType, double embedmentMm, FloorType? lotType, double lotOffsetMm)
    {
        using var write = new WriteSession(context, document, "BIMTECH MCP: Đặt cọc + đài móng theo CAD");
        var symbols = plans.ToDictionary(plan => plan.Group, plan => plan.Symbol);
        var createdTypes = new List<FamilySymbol>();
        var typeErrors = new List<string>();
        var toCreate = plans.Where(plan => plan.Source == TypeSource.CreateType).Select(plan => (plan.Group, plan.Template)).ToList();
        if (toCreate.Count > 0)
        {
            var typeSummary = PileFoundationPlacer.CreateMissingTypes(document, toCreate);
            createdTypes = typeSummary.CreatedSymbols;
            typeErrors = typeSummary.Errors;
            foreach (var (group, symbol) in typeSummary.Resolved) symbols[group] = symbol;
        }

        var assignments = plans.Select(plan => new PileAssignment(plan.Group, symbols[plan.Group])).ToList();
        var result = PileWorkflow.PlaceAll(
            document, view, assignments, scan.Caps, capLevel, capType, embedmentMm,
            lotType, lotOffsetMm, PileTypeMatcher.CollectFoundationTypes(document));

        var place = result.Place;
        var created = place.CreatedPileIds.Concat(place.CreatedCapIds).Concat(result.Lot?.CreatedLotFloorIds ?? []).ToList();
        var batchId = write.Complete(
            $"Đặt {place.PilesCreated} cọc, {place.CapsCreated} đài", created, createdTypes.Select(type => type.Id).ToList());

        return new
        {
            dryRun = false,
            batchId,
            piles = new { created = place.PilesCreated, skippedDuplicates = place.PilesSkipped, failed = place.PilesFailed },
            caps = new { created = place.CapsCreated, skippedDuplicates = place.CapsSkipped, failed = place.CapsFailed },
            leanConcrete = result.Lot is null ? null : new { created = result.Lot.Created, skipped = result.Lot.Skipped, failed = result.Lot.Failed },
            gridRounding = result.Snap is null ? null : new { adjusted = result.Snap.Snapped, alreadyRound = result.Snap.AlreadyRound, noParallelGrid = result.Snap.SkippedNoGrid },
            joins = result.Join is null ? null : new { joined = result.Join.Joined, alreadyJoined = result.Join.AlreadyJoined, failed = result.Join.Failed },
            errors = typeErrors.Concat(place.Errors).ToList(),
            typesCreated = createdTypes.Select(StructuralJson.PileType).ToList(),
            revitWarnings = write.WarningReport,
            undo = batchId is null ? null : StructuralJson.UndoHint,
        };
    }
}
