using Bimtech.RevitMcp.Planning;

namespace Bimtech.RevitMcp.Commands.Plans;

// Reads a plan with its items, one page at a time. Filters let Claude answer
// questions such as "which items are blocked and why?" from the stored reasons.
public sealed class GetPlanCommand(PlanService plans) : IRevitCommand
{
    public string Method => "get_plan";

    public object? Execute(CommandContext context)
    {
        var p = context.Params;
        var document = PlanStorageLocation.For(context.RequireDocument());
        var plan = plans.Get(document, p.RequireString("planId", 64));

        PlanItemStatus? itemStatus = p.Has("itemStatus") ? p.GetEnum("itemStatus", PlanItemStatus.Pending) : null;
        PlanTier? tier = p.Has("tier") ? p.GetEnum("tier", PlanTier.Review) : null;
        var itemIds = p.GetStringList("itemIds", maxCount: 1000, maxLength: 64).ToHashSet(StringComparer.Ordinal);
        var offset = p.GetInt("offset", 0, min: 0);
        var limit = p.GetInt("limit", 50, min: 1, max: 200);

        var matched = plan.Items
            .Where(item => itemStatus is null || item.Status == itemStatus)
            .Where(item => tier is null || item.Tier == tier)
            .Where(item => itemIds.Count == 0 || itemIds.Contains(item.ItemId))
            .ToList();

        return new
        {
            plan = PlanResults.Status(plan),
            document = plan.Document,
            scope = plan.Scope,
            questions = plan.Questions,
            items = matched.Skip(offset).Take(limit),
            page = new { offset, limit, matched = matched.Count, hasMore = offset + limit < matched.Count },
        };
    }
}
