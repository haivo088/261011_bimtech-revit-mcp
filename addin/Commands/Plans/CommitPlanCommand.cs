using Bimtech.RevitMcp.Planning;
using Bimtech.RevitMcp.Planning.Actions;
using Bimtech.RevitMcp.Protocol;

namespace Bimtech.RevitMcp.Commands.Plans;

// Writes the items the person approved in the review window. Needs the approvalId that
// get_plan_status shows after approval; refuses stale plans and plans whose approved items changed.
// Calling it again on a committed plan returns the stored results and writes nothing.
// The review window uses Write too, when the person presses "Duyệt kế hoạch" with CommitOnApprove on.
public sealed class CommitPlanCommand(PlanService plans, PlanCommitter committer, PlanActions actions, ModelChangeTracker changes)
    : IRevitCommand
{
    public string Method => "commit_plan";
    // A plan may hold items that overwrite or delete, so a commit counts as Delete.
    public CommandAccess Access => CommandAccess.Delete;

    private const int MaxItemsReturned = 200;

    public object? Execute(CommandContext context)
    {
        var result = Write(context.RequireDocument(), context.Params.RequireString("planId", 64), context.Params.RequireString("approvalId", 64));
        var plan = result.Plan;
        var written = plan.Items.Where(item => item.Status is PlanItemStatus.Applied or PlanItemStatus.Failed).ToList();

        return new
        {
            plan = PlanResults.Status(plan),
            alreadyCommitted = result.AlreadyCommitted,
            applied = written.Count(item => item.Status == PlanItemStatus.Applied),
            failed = written.Count(item => item.Status == PlanItemStatus.Failed),
            items = written.Take(MaxItemsReturned).Select(item => new { item.ItemId, item.Status, item.Result }),
            undo = "The whole commit is one undo step in Revit (Ctrl+Z).",
        };
    }

    // Must run in a Revit API context. Throws RpcException when the plan cannot be written (stale, wrong approval...).
    public PlanCommitResult Write(Autodesk.Revit.DB.Document revitDocument, string planId, string approvalId)
    {
        var document = PlanStorageLocation.For(revitDocument);

        // A model that cannot be written is reported up front, instead of every item failing on its own.
        if (plans.Get(document, planId).Status != PlanStatus.Committed)
        {
            if (revitDocument.IsReadOnly)
                throw new RpcException(RpcErrorCodes.PlanWrongStatus, "The model is read-only; nothing can be written.", new { planId });
            if (revitDocument.IsModifiable)
                throw new RpcException(RpcErrorCodes.PlanWrongStatus,
                    "Revit is inside another command or transaction. Ask the user to finish it, then commit again.", new { planId });
        }

        var writer = new RevitPlanWriter(revitDocument, actions, $"BIMTECH: {plans.Get(document, planId).Title}");
        return committer.Commit(document, planId, approvalId, changes.Current(revitDocument), writer);
    }
}
