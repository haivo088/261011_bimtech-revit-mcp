using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands.Common;
using Bimtech.RevitMcp.Protocol;
using Bimtech.StructuralCore.Columns;

namespace Bimtech.RevitMcp.Commands.Structural;

// Places the columns of a scan between two levels.
//   dryRun (default true)  reports what would happen and changes nothing;
//   dryRun false           creates missing types and columns inside one TransactionGroup (one Ctrl+Z in Revit)
//                          and returns a batchId for undo_batch.
// Type for each size group, in order: "types" entry → existing type with matching b/h → new type duplicated
// in templateFamilyId → unresolved. A real run refuses to start while any group is unresolved.
public sealed class PlaceColumnsCommand : IRevitCommand
{
    public string Method => "place_columns";
    public CommandAccess Access => CommandAccess.Write;

    // A dry run only reads, so previews stay available in read-only mode.
    public bool ModifiesModel(CommandParams parameters) => !parameters.GetBool("dryRun", true);

    private enum TypeSource { Assigned, Exact, ReuseInFamily, CreateType }

    private sealed record GroupPlan(ColumnSizeGroup Group, FamilySymbol? Symbol, TypeSource? Source, string? NewTypeName, string? Problem);

    public object? Execute(CommandContext context)
    {
        var p = context.Params;
        var dryRun = p.GetBool("dryRun", true);
        var document = dryRun ? context.RequireDocument() : context.RequireWritableDocument();
        var scanId = p.RequireString("scanId", maxLength: 64);
        var scan = context.Sessions.Require<ColumnScan>(ColumnScan.Kind, scanId, document, "scanId");
        var baseLevel = StructuralJson.RequireLevel(document, p.RequireElementId("baseLevelId"), "baseLevelId");
        var topLevel = StructuralJson.RequireLevel(document, p.RequireElementId("topLevelId"), "topLevelId");
        if (topLevel.Elevation <= baseLevel.Elevation)
            throw RpcException.InvalidParams("topLevelId", "must be higher than baseLevelId.");

        var total = scan.Groups.Sum(group => group.Columns.Count);
        var limit = context.Settings.MaxElementsPerWrite;
        if (total > limit)
            throw RpcException.InvalidParams("scanId",
                $"the scan has {total} columns, above the limit of {limit} per call set in BIMTECH MCP Settings. "
                + "Scan a smaller region, or ask the user to raise the limit.");

        var templateFamily = p.Has("templateFamilyId")
            ? StructuralJson.RequireFamily(document, p.RequireElementId("templateFamilyId"), BuiltInCategory.OST_StructuralColumns)
            : null;
        var plans = Resolve(document, scan.Groups, p.GetStringMap("types", maxCount: 200, maxLength: 32), templateFamily);

        var unresolved = plans.Where(plan => plan.Source is null).ToList();
        if (!dryRun && unresolved.Count > 0)
            throw RpcException.InvalidParams("types",
                $"no type for group(s) {string.Join(", ", unresolved.Select(plan => plan.Group.Key))}. "
                + "Assign a type in 'types', or pass templateFamilyId to create missing types. Use dryRun=true to review.");

        return dryRun
            ? Preview(document, scanId, plans, templateFamily, baseLevel, topLevel)
            : Run(context, document, plans, templateFamily, baseLevel, topLevel);
    }

    private static List<GroupPlan> Resolve(
        Document document, IReadOnlyList<ColumnSizeGroup> groups, IReadOnlyDictionary<string, string> assigned, Family? templateFamily)
    {
        var unknownKeys = assigned.Keys.Except(groups.Select(group => group.Key)).ToList();
        if (unknownKeys.Count > 0)
            throw RpcException.InvalidParams("types",
                $"unknown group key(s) {string.Join(", ", unknownKeys)}. Valid keys: {string.Join(", ", groups.Select(group => group.Key))}.");

        var available = ColumnTypeMatcher.CollectColumnTypes(document);
        var plans = new List<GroupPlan>();
        var needTypes = new List<ColumnSizeGroup>();
        foreach (var group in groups)
        {
            if (assigned.TryGetValue(group.Key, out var typeId))
            {
                plans.Add(new GroupPlan(group, StructuralJson.RequireType(document, typeId, $"types.{group.Key}",
                    BuiltInCategory.OST_StructuralColumns, "scan_cad_columns"), TypeSource.Assigned, null, null));
                continue;
            }
            var exact = ColumnTypeMatcher.FindExactMatch(group.SizeAMm, group.SizeBMm, available, s => s);
            if (exact is not null)
                plans.Add(new GroupPlan(group, exact, TypeSource.Exact, null, null));
            else if (templateFamily is not null)
                needTypes.Add(group);
            else
                plans.Add(new GroupPlan(group, null, null, null, "no type with matching b/h"));
        }

        if (templateFamily is not null)
        {
            foreach (var preview in ColumnPlacer.PlanMissingTypes(document, needTypes, templateFamily))
            {
                plans.Add(preview switch
                {
                    { Reuse: { } reuse } => new GroupPlan(preview.Group, reuse, TypeSource.ReuseInFamily, null, null),
                    { NewTypeName: { } name } => new GroupPlan(preview.Group, null, TypeSource.CreateType, name, null),
                    _ => new GroupPlan(preview.Group, null, null, null, preview.Error),
                });
            }
        }

        // Keep the scan's order (by size) so previews and results read the same way.
        var order = groups.Select((group, index) => (group, index)).ToDictionary(entry => entry.group, entry => entry.index);
        return plans.OrderBy(plan => order[plan.Group]).ToList();
    }

    private static object Preview(
        Document document, string scanId, List<GroupPlan> plans, Family? templateFamily, Level baseLevel, Level topLevel)
    {
        // Duplicates are found per family, so a type that does not exist yet is checked with any type of
        // the family it will be created in.
        var templateStandIn = templateFamily?.GetFamilySymbolIds().Select(document.GetElement).OfType<FamilySymbol>().FirstOrDefault();
        var assignments = plans
            .Select(plan => new ColumnAssignment(plan.Group, plan.Symbol ?? (plan.Source == TypeSource.CreateType ? templateStandIn : null)))
            .ToList();
        var placement = ColumnPlacer.PlanPlacement(document, assignments, baseLevel, topLevel);

        var groups = plans.Zip(placement, (plan, place) => new
        {
            key = plan.Group.Key,
            count = plan.Group.Columns.Count,
            type = plan.Symbol is null ? null : StructuralJson.ColumnType(plan.Symbol),
            typeAction = plan.Source?.ToString(),
            newTypeName = plan.NewTypeName,
            toCreate = place.ToCreate,
            duplicates = place.Duplicates,
            problem = plan.Problem ?? place.Problem,
        }).ToList();

        return new
        {
            dryRun = true,
            scanId,
            baseLevel = StructuralJson.Ref(baseLevel),
            topLevel = StructuralJson.Ref(topLevel),
            totals = new
            {
                toCreate = placement.Sum(place => place.ToCreate),
                duplicates = placement.Sum(place => place.Duplicates),
                newTypes = plans.Count(plan => plan.Source == TypeSource.CreateType),
                unresolvedGroups = plans.Count(plan => plan.Source is null),
            },
            groups,
            next = plans.Any(plan => plan.Source is null)
                ? "Resolve the groups with a problem (types or templateFamilyId), then preview again."
                : "Show this plan to the user. If they confirm, call place_columns again with the same arguments and dryRun=false.",
        };
    }

    private static object Run(
        CommandContext context, Document document, List<GroupPlan> plans, Family? templateFamily, Level baseLevel, Level topLevel)
    {
        using var write = new WriteSession(context, document, "BIMTECH MCP: Đặt cột theo CAD");
        var symbols = plans.ToDictionary(plan => plan.Group, plan => plan.Symbol);
        var createdTypes = new List<FamilySymbol>();
        var typeErrors = new List<string>();
        var toCreate = plans.Where(plan => plan.Source == TypeSource.CreateType).Select(plan => plan.Group).ToList();
        if (toCreate.Count > 0 && templateFamily is not null)
        {
            var typeSummary = ColumnPlacer.CreateMissingTypes(document, toCreate, templateFamily);
            createdTypes = typeSummary.CreatedSymbols;
            typeErrors = typeSummary.Errors;
            foreach (var (group, symbol) in typeSummary.Resolved) symbols[group] = symbol;
        }

        var assignments = plans.Select(plan => new ColumnAssignment(plan.Group, symbols[plan.Group])).ToList();
        var summary = ColumnPlacer.Place(document, assignments, baseLevel, topLevel);
        var batchId = write.Complete($"Đặt {summary.Created} cột", summary.CreatedIds, createdTypes.Select(type => type.Id).ToList());

        return new
        {
            dryRun = false,
            batchId,
            created = summary.Created,
            skippedDuplicates = summary.Skipped,
            failed = summary.Failed,
            errors = typeErrors.Concat(summary.Errors).ToList(),
            typesCreated = createdTypes.Select(StructuralJson.ColumnType).ToList(),
            revitWarnings = write.WarningReport,
            undo = batchId is null ? null : StructuralJson.UndoHint,
        };
    }
}
