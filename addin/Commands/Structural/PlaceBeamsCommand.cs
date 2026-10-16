using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands.Common;
using Bimtech.RevitMcp.Protocol;
using Bimtech.StructuralCore.Beams;

namespace Bimtech.RevitMcp.Commands.Structural;

// Places the beams of a scan on a level:
//   dryRun (default true) reports the plan; dryRun false runs in one WriteSession and returns a batchId.
// Which groups: those with a tag name, plus "include", minus "exclude".
// Type per group: "types" entry → existing type with matching b×h → new type in templateFamilyId (needs h) → unresolved.
public sealed class PlaceBeamsCommand : IRevitCommand
{
    public string Method => "place_beams";
    public CommandAccess Access => CommandAccess.Write;

    // A dry run only reads, so previews stay available in read-only mode.
    public bool ModifiesModel(CommandParams parameters) => !parameters.GetBool("dryRun", true);

    private enum TypeSource { Assigned, Exact, ReuseInFamily, CreateType }

    private sealed record GroupPlan(
        BeamSizeGroup Group, FamilySymbol? Symbol, TypeSource? Source, string? NewTypeName, string? Problem, FamilySymbol? Nearest);

    public object? Execute(CommandContext context)
    {
        var p = context.Params;
        var dryRun = p.GetBool("dryRun", true);
        var document = dryRun ? context.RequireDocument() : context.RequireWritableDocument();
        var scanId = p.RequireString("scanId", maxLength: 64);
        var scan = context.Sessions.Require<BeamScan>(BeamScan.Kind, scanId, document, "scanId");
        var level = StructuralJson.RequireLevel(document, p.RequireElementId("levelId"), "levelId");
        var tagParameterName = p.GetString("tagParameterName", maxLength: 200);

        var keys = scan.Groups.Select(group => group.Key).ToList();
        var include = p.GetStringList("include", maxCount: 500, maxLength: 200);
        var exclude = p.GetStringList("exclude", maxCount: 500, maxLength: 200);
        var assigned = p.GetStringMap("types", maxCount: 500, maxLength: 32);
        StructuralJson.RequireKnownKeys(include, keys, "include");
        StructuralJson.RequireKnownKeys(exclude, keys, "exclude");
        StructuralJson.RequireKnownKeys(assigned.Keys, keys, "types");

        var selected = scan.Groups
            .Where(group => (group.ModelByDefault || include.Contains(group.Key)) && !exclude.Contains(group.Key))
            .ToList();
        if (selected.Count == 0)
            throw RpcException.InvalidParams("include", "no group is selected. Groups without a tag name must be listed in 'include'.");

        var total = selected.Sum(group => group.Beams.Count);
        var limit = context.Settings.MaxElementsPerWrite;
        if (total > limit)
            throw RpcException.InvalidParams("scanId",
                $"the selected groups have {total} beams, above the limit of {limit} per call set in BIMTECH MCP Settings. "
                + "Exclude some groups, scan a smaller region, or ask the user to raise the limit.");

        var templateFamily = p.Has("templateFamilyId")
            ? StructuralJson.RequireFamily(document, p.RequireElementId("templateFamilyId"), BuiltInCategory.OST_StructuralFraming)
            : null;
        var plans = Resolve(document, selected, assigned, templateFamily);

        var unresolved = plans.Where(plan => plan.Source is null).ToList();
        if (!dryRun && unresolved.Count > 0)
            throw RpcException.InvalidParams("types",
                $"no type for group(s) {string.Join(", ", unresolved.Select(plan => plan.Group.Key))}. "
                + "Assign a type in 'types', pass templateFamilyId, or exclude them. Use dryRun=true to review.");

        return dryRun
            ? Preview(document, scanId, scan, plans, templateFamily, level)
            : Run(context, document, plans, templateFamily, level, tagParameterName);
    }

    private static List<GroupPlan> Resolve(
        Document document, List<BeamSizeGroup> groups, IReadOnlyDictionary<string, string> assigned, Family? templateFamily)
    {
        var available = BeamTypeMatcher.CollectBeamTypes(document);
        var plans = new List<GroupPlan>();
        var needTypes = new List<BeamSizeGroup>();
        foreach (var group in groups)
        {
            var (suggested, isExact) = BeamPlacer.SuggestType(group, available, s => s);
            if (assigned.TryGetValue(group.Key, out var typeId))
                plans.Add(new GroupPlan(group, StructuralJson.RequireType(document, typeId, $"types.{group.Key}",
                    BuiltInCategory.OST_StructuralFraming, "scan_cad_beams"), TypeSource.Assigned, null, null, null));
            else if (isExact)
                plans.Add(new GroupPlan(group, suggested, TypeSource.Exact, null, null, null));
            else if (templateFamily is not null && group.SizeHMm is not null)
                needTypes.Add(group);
            else
                plans.Add(new GroupPlan(group, null, null, null,
                    group.SizeHMm is null ? "height unknown (no text): assign a type in 'types'" : "no type with matching b×h", suggested));
        }

        if (templateFamily is not null)
        {
            foreach (var (group, reuse, newTypeName, error) in BeamPlacer.PlanMissingTypes(document, needTypes, templateFamily))
            {
                plans.Add(reuse is not null ? new GroupPlan(group, reuse, TypeSource.ReuseInFamily, null, null, null)
                    : newTypeName is not null ? new GroupPlan(group, null, TypeSource.CreateType, newTypeName, null, null)
                    : new GroupPlan(group, null, null, null, error, null));
            }
        }

        var order = groups.Select((group, index) => (group, index)).ToDictionary(entry => entry.group, entry => entry.index);
        return plans.OrderBy(plan => order[plan.Group]).ToList();
    }

    private static object Preview(
        Document document, string scanId, BeamScan scan, List<GroupPlan> plans, Family? templateFamily, Level level)
    {
        // Duplicates of beams do not depend on the type, so a future type can stand in with any type of its family.
        var standIn = templateFamily?.GetFamilySymbolIds().Select(document.GetElement).OfType<FamilySymbol>().FirstOrDefault();
        var assignments = plans
            .Select(plan => new BeamAssignment(plan.Group, plan.Symbol ?? (plan.Source == TypeSource.CreateType ? standIn : null)))
            .ToList();
        var placement = BeamPlacer.PlanPlacement(document, assignments, level);
        var notSelected = scan.Groups.Except(plans.Select(plan => plan.Group)).ToList();

        return new
        {
            dryRun = true,
            scanId,
            level = StructuralJson.Ref(level),
            totals = new
            {
                toCreate = placement.Sum(place => place.ToCreate),
                duplicates = placement.Sum(place => place.Duplicates),
                newTypes = plans.Where(plan => plan.Source == TypeSource.CreateType).Select(plan => plan.NewTypeName).Distinct().Count(),
                unresolvedGroups = plans.Count(plan => plan.Source is null),
                groupsNotSelected = notSelected.Count,
                beamsNotSelected = notSelected.Sum(group => group.Beams.Count),
            },
            groups = plans.Zip(placement, (plan, place) => new
            {
                key = plan.Group.Key,
                count = plan.Group.Beams.Count,
                type = plan.Symbol is null ? null : StructuralJson.BeamType(plan.Symbol),
                typeAction = plan.Source?.ToString(),
                newTypeName = plan.NewTypeName,
                nearestType = plan.Nearest is null ? null : StructuralJson.BeamType(plan.Nearest),
                toCreate = place.ToCreate,
                duplicates = place.Duplicates,
                problem = plan.Problem ?? place.Problem,
            }).ToList(),
            notSelectedKeys = notSelected.Select(group => group.Key).ToList(),
            next = plans.Any(plan => plan.Source is null)
                ? "Resolve the groups with a problem (types, templateFamilyId, or exclude), then preview again."
                : "Show this plan to the user. If they confirm, call place_beams again with the same arguments and dryRun=false.",
        };
    }

    private static object Run(
        CommandContext context, Document document, List<GroupPlan> plans, Family? templateFamily, Level level, string? tagParameterName)
    {
        using var write = new WriteSession(context, document, "BIMTECH MCP: Đặt dầm theo CAD");
        var symbols = plans.ToDictionary(plan => plan.Group, plan => plan.Symbol);
        var createdTypes = new List<FamilySymbol>();
        var typeErrors = new List<string>();
        var toCreate = plans.Where(plan => plan.Source == TypeSource.CreateType).Select(plan => plan.Group).ToList();
        if (toCreate.Count > 0 && templateFamily is not null)
        {
            var typeSummary = BeamPlacer.CreateMissingTypes(document, toCreate, templateFamily);
            createdTypes = typeSummary.CreatedSymbols;
            typeErrors = typeSummary.Errors;
            foreach (var (group, symbol) in typeSummary.Resolved) symbols[group] = symbol;
        }

        var assignments = plans.Select(plan => new BeamAssignment(plan.Group, symbols[plan.Group])).ToList();
        var summary = BeamPlacer.Place(document, assignments, level, tagParameterName);
        var batchId = write.Complete($"Đặt {summary.Created} dầm", summary.CreatedIds, createdTypes.Select(type => type.Id).ToList());

        return new
        {
            dryRun = false,
            batchId,
            created = summary.Created,
            skippedDuplicates = summary.Skipped,
            failed = summary.Failed,
            errors = typeErrors.Concat(summary.Errors).ToList(),
            typesCreated = createdTypes.Select(StructuralJson.BeamType).ToList(),
            revitWarnings = write.WarningReport,
            undo = batchId is null ? null : StructuralJson.UndoHint,
        };
    }
}
