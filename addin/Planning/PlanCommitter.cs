using System.Text.Json;
using System.Text.Json.Nodes;
using Bimtech.RevitMcp.Protocol;

namespace Bimtech.RevitMcp.Planning;

// What happened to one approved item at commit: written (Applied) or not (Failed), with a result
// such as created element IDs, or the error message.
public sealed record PlanItemOutcome(string ItemId, bool Applied, JsonElement Result);

// Writes approved items into the model. The Revit implementation (RevitPlanWriter) wraps all items in
// one TransactionGroup, so the person undoes a commit in one step; tests use a fake.
public interface IPlanItemWriter
{
    // Returns one outcome per item, in the same order. Never throws for a single bad item.
    IReadOnlyList<PlanItemOutcome> Apply(IReadOnlyList<PlanItem> items);
}

// Result of commit_plan. AlreadyCommitted is true when the plan had been committed before and the
// stored results were returned without writing anything again.
public sealed record PlanCommitResult(Plan Plan, bool AlreadyCommitted);

// The rules of commit_plan (docs/04-architecture.md, principles 2–4), kept free of Revit types so
// they are tested without Revit. The Revit part is only the IPlanItemWriter it is given.
public sealed class PlanCommitter(PlanService plans, IPlanStore store, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public PlanCommitResult Commit(
        PlanDocument document, string planId, string approvalId, ModelStamp currentStamp, IPlanItemWriter writer)
    {
        var plan = plans.Get(document, planId);

        // Calling commit again returns what the first call wrote; nothing is created twice.
        if (plan.Status == PlanStatus.Committed)
        {
            RequireApproval(plan, approvalId);
            return new PlanCommitResult(plan, AlreadyCommitted: true);
        }
        if (plan.Status == PlanStatus.Stale)
            throw StaleError(plan, currentStamp, "The plan is stale.");
        PlanTransitions.Ensure(plan, PlanStatus.Committed);
        RequireApproval(plan, approvalId);

        // The approval froze a hash of the approved items. If the stored items no longer match it
        // (e.g. the plan file was edited), what would be written is not what the person approved.
        var approval = plan.Approval!;
        var byId = plan.Items.ToDictionary(item => item.ItemId, StringComparer.Ordinal);
        var approvedItems = approval.ApprovedItemIds
            .Select(id => byId.TryGetValue(id, out var item) && item.Status == PlanItemStatus.Approved ? item : null)
            .ToList();
        if (approvedItems.Any(item => item is null)
            || PlanApproval.HashItems(approvedItems!) != approval.ItemsHash)
            throw new RpcException(RpcErrorCodes.ApprovalMismatch,
                "The approved items changed after approval. Ask the user to review and approve the plan again.",
                new { planId, approvalId });

        // Items were checked against the model as it was when proposed. Any change since may make them wrong.
        if (plan.ModelStamp != currentStamp)
        {
            plans.Move(document, planId, PlanStatus.Stale);
            throw StaleError(plan, currentStamp, plan.ModelStamp is null
                ? "The plan was proposed before change tracking existed."
                : plan.ModelStamp.SessionId != currentStamp.SessionId
                    ? "Revit was restarted or the model reopened since the plan was proposed."
                    : "The model changed since the plan was proposed.");
        }

        var outcomes = writer.Apply(approvedItems!);
        var committed = plans.MarkCommitted(document, planId, outcomes);
        store.AppendAudit(document, new PlanAuditEntry(
            _clock.GetUtcNow(), committed.PlanId, committed.Kind, committed.Title, approval.ApprovalId,
            Environment.UserName,
            outcomes.Count(outcome => outcome.Applied), outcomes.Count(outcome => !outcome.Applied)));
        return new PlanCommitResult(committed, AlreadyCommitted: false);
    }

    private static void RequireApproval(Plan plan, string approvalId)
    {
        if (plan.Approval is null || plan.Approval.ApprovalId != approvalId)
            throw new RpcException(RpcErrorCodes.ApprovalMismatch,
                "approvalId does not match the user's latest approval. Call get_plan_status and use its approvalId; "
                + "never guess one.",
                new { planId = plan.PlanId, approvalId });
    }

    private static RpcException StaleError(Plan plan, ModelStamp current, string reason) =>
        new(RpcErrorCodes.PlanStale,
            $"{reason} Nothing was written. Propose again to check the items against the current model.",
            new { planId = plan.PlanId, proposedStamp = plan.ModelStamp, currentStamp = current });
}

// One line of the audit log, written after every commit that reached the model.
public sealed record PlanAuditEntry(
    DateTimeOffset At, string PlanId, string Kind, string Title, string ApprovalId, string User, int Applied, int Failed);

// The params an action executes: the proposed params with the reviewer's overrides merged on top
// (step 2b fills overrides; until then they are null and params are used as proposed).
public static class PlanItemParams
{
    public static JsonElement Effective(PlanItem item)
    {
        if (item.Overrides is not { ValueKind: JsonValueKind.Object } overrides) return item.Params;
        var merged = JsonNode.Parse(item.Params.GetRawText())!.AsObject();
        foreach (var property in overrides.EnumerateObject())
            merged[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        return JsonSerializer.SerializeToElement(merged);
    }
}
