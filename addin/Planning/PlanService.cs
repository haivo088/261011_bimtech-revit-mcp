using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bimtech.RevitMcp.Protocol;

namespace Bimtech.RevitMcp.Planning;

// Creates plans, looks them up and moves them between statuses.
// Only the transitions in PlanTransitions are allowed, and only the review window
// calls Approve; no JSON-RPC command may (server/tests/contract.test.mjs checks this).
// autoApproveSure: the person's "auto-commit sure plans" setting. When it says yes, a new plan whose every item is
// sure (none pending, none blocked) is approved at once, as the person asked; no command ever approves on its own.
public sealed class PlanService(IPlanStore store, TimeProvider? clock = null, Func<bool>? autoApproveSure = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    // Commands and the review window both run on Revit's main thread today;
    // the lock keeps the service safe if that ever changes.
    private readonly object _lock = new();

    // Raised after every saved change, so an open review window can refresh
    // when Claude cancels a plan or the person edits it.
    public event Action<Plan>? Changed;

    public Plan Create(PlanDocument document, string kind, string title, JsonElement? scope,
        IEnumerable<PlanItem> items, IEnumerable<PlanQuestion>? questions = null, ModelStamp? modelStamp = null)
    {
        var plan = new Plan
        {
            PlanId = PlanIds.New(_clock.GetUtcNow()),
            Kind = kind,
            Title = title,
            CreatedAt = _clock.GetUtcNow(),
            UpdatedAt = _clock.GetUtcNow(),
            Document = document,
            Scope = scope,
            Items = items.ToList(),
            Questions = questions?.ToList() ?? [],
            ModelStamp = modelStamp,
        };
        Validate(plan);
        PlanConfidence.Apply(plan.Items);
        if (autoApproveSure?.Invoke() == true
            && plan.Items.All(item => item.Tier == PlanTier.Auto && item.Status == PlanItemStatus.Approved))
            RecordApproval(plan, automatic: true);
        lock (_lock) store.Save(plan);
        Changed?.Invoke(plan);
        return plan;
    }

    public Plan Get(PlanDocument document, string planId)
    {
        PlanIds.Require(planId);
        lock (_lock)
        {
            return store.Load(document, planId) ?? throw new RpcException(RpcErrorCodes.PlanNotFound,
                $"No plan '{planId}' for the open model. Plans belong to the model they were proposed in.",
                new { planId });
        }
    }

    public Plan Cancel(PlanDocument document, string planId) => Move(document, planId, PlanStatus.Cancelled);

    // Moves a plan to any status except Approved, which only Approve may set.
    public Plan Move(PlanDocument document, string planId, PlanStatus target)
    {
        if (target == PlanStatus.Approved)
            throw new InvalidOperationException("Only the review window approves plans; call Approve.");
        return Update(document, planId, plan =>
        {
            PlanTransitions.Ensure(plan, target);
            plan.Status = target;
            // A committed plan keeps its approval as a record of what was approved.
            if (target != PlanStatus.Committed) plan.Approval = null;
            return true;
        });
    }

    // Records the reviewer's decision on some items. Pending undoes a decision.
    // Editing an approved plan sends it back to InReview, so its approvalId stops working.
    public Plan SetItemStatus(PlanDocument document, string planId, IReadOnlyCollection<string> itemIds, PlanItemStatus status)
    {
        if (status is not (PlanItemStatus.Pending or PlanItemStatus.Approved or PlanItemStatus.Rejected))
            throw new ArgumentOutOfRangeException(nameof(status), status, "Reviewers can only set Pending, Approved or Rejected.");
        return Update(document, planId, plan =>
        {
            var byId = plan.Items.ToDictionary(item => item.ItemId, StringComparer.Ordinal);
            var unknown = itemIds.Where(id => !byId.ContainsKey(id)).ToList();
            if (unknown.Count > 0)
                throw RpcException.InvalidParams("itemIds", $"plan '{planId}' has no items {string.Join(", ", unknown)}.");

            var changing = itemIds.Select(id => byId[id]).Where(item => item.Status != status).ToList();
            if (changing.Count == 0) return false;
            if (status == PlanItemStatus.Approved && changing.FirstOrDefault(item => item.Tier == PlanTier.Blocked) is { } blocked)
                throw new RpcException(RpcErrorCodes.PlanWrongStatus,
                    $"Item '{blocked.ItemId}' is blocked and cannot be approved: {string.Join(" ", blocked.Reasons)}",
                    new { planId, itemId = blocked.ItemId });

            if (plan.Status != PlanStatus.InReview)
            {
                PlanTransitions.Ensure(plan, PlanStatus.InReview);
                plan.Status = PlanStatus.InReview;
                plan.Approval = null;
            }
            foreach (var item in changing) item.Status = status;
            return true;
        });
    }

    // Called only by the review window when the person presses "Approve plan".
    // Every reviewable item must be decided first; blocked items stay out of the plan.
    public Plan Approve(PlanDocument document, string planId) => Update(document, planId, plan =>
    {
        PlanTransitions.Ensure(plan, PlanStatus.Approved);
        var undecided = plan.Items.Count(item => item.Tier != PlanTier.Blocked && item.Status == PlanItemStatus.Pending);
        if (undecided > 0)
            throw new RpcException(RpcErrorCodes.PlanWrongStatus,
                $"{undecided} item(s) still need a decision. Approve or reject them first.",
                new { planId, undecided });
        var approved = plan.Items.Where(item => item.Status == PlanItemStatus.Approved).ToList();
        if (approved.Count == 0)
            throw new RpcException(RpcErrorCodes.PlanWrongStatus,
                "No item is approved. Approve at least one item, or cancel the plan.", new { planId });

        RecordApproval(plan, automatic: false);
        return true;
    });

    private void RecordApproval(Plan plan, bool automatic)
    {
        var approved = plan.Items.Where(item => item.Status == PlanItemStatus.Approved).ToList();
        plan.Status = PlanStatus.Approved;
        plan.Approval = new PlanApproval
        {
            ApprovalId = ApprovalIds.New(),
            ApprovedAt = _clock.GetUtcNow(),
            ApprovedItemIds = approved.Select(item => item.ItemId).ToList(),
            ItemsHash = PlanApproval.HashItems(approved),
            Automatic = automatic,
        };
    }

    // Records what commit_plan wrote: each approved item becomes Applied or Failed with its result,
    // and the plan becomes Committed. The approval stays as a record of what was approved.
    // Only PlanCommitter calls this, after the model was written.
    public Plan MarkCommitted(PlanDocument document, string planId, IReadOnlyCollection<PlanItemOutcome> outcomes) =>
        Update(document, planId, plan =>
        {
            PlanTransitions.Ensure(plan, PlanStatus.Committed);
            var byId = plan.Items.ToDictionary(item => item.ItemId, StringComparer.Ordinal);
            foreach (var outcome in outcomes)
            {
                if (!byId.TryGetValue(outcome.ItemId, out var item))
                    throw new InvalidOperationException($"Plan '{planId}' has no item '{outcome.ItemId}'.");
                item.Status = outcome.Applied ? PlanItemStatus.Applied : PlanItemStatus.Failed;
                item.Result = outcome.Result;
            }
            plan.Status = PlanStatus.Committed;
            return true;
        });

    // Loads, changes and saves a plan under the lock. The change returns false when
    // nothing changed, so the file and UpdatedAt are left alone.
    private Plan Update(PlanDocument document, string planId, Func<Plan, bool> change)
    {
        Plan plan;
        lock (_lock)
        {
            plan = Get(document, planId);
            if (!change(plan)) return plan;
            plan.UpdatedAt = _clock.GetUtcNow();
            store.Save(plan);
        }
        // Outside the lock, so a listener may call back into the service.
        Changed?.Invoke(plan);
        return plan;
    }

    private static void Validate(Plan plan)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in plan.Items)
        {
            if (string.IsNullOrWhiteSpace(item.ItemId) || !ids.Add(item.ItemId))
                throw new InvalidOperationException($"Plan item IDs must be unique and non-empty: '{item.ItemId}'.");
            if (item.Params.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"Plan item '{item.ItemId}' needs params as a JSON object.");
        }
    }
}

public static class PlanTransitions
{
    private static readonly Dictionary<PlanStatus, PlanStatus[]> Allowed = new()
    {
        [PlanStatus.InReview] = [PlanStatus.Approved, PlanStatus.Stale, PlanStatus.Cancelled],
        [PlanStatus.Approved] = [PlanStatus.InReview, PlanStatus.Committed, PlanStatus.Stale, PlanStatus.Cancelled],
        [PlanStatus.Committed] = [],
        [PlanStatus.Stale] = [],
        [PlanStatus.Cancelled] = [],
    };

    public static bool CanMove(PlanStatus from, PlanStatus to) => Allowed[from].Contains(to);

    public static void Ensure(Plan plan, PlanStatus target)
    {
        if (!CanMove(plan.Status, target))
            throw new RpcException(RpcErrorCodes.PlanWrongStatus,
                $"Plan '{plan.PlanId}' is {plan.Status} and cannot become {target}.",
                new { planId = plan.PlanId, status = plan.Status.ToString(), target = target.ToString() });
    }
}

// IDs look like "pln_20261014_8f3a2c". They become file names, so anything else is rejected.
public static partial class PlanIds
{
    [GeneratedRegex("^pln_[0-9]{8}_[0-9a-f]{6}$")]
    private static partial Regex Pattern();

    public static string New(DateTimeOffset now) =>
        $"pln_{now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant()}";

    public static bool IsValid(string? planId) => planId is not null && Pattern().IsMatch(planId);

    public static void Require(string planId)
    {
        if (!IsValid(planId))
            throw RpcException.InvalidParams("planId", "expected an ID such as \"pln_20261014_8f3a2c\".");
    }
}

// Approval IDs look like "apr_3f9c0e7a1b2d4c68". They are random, so Claude cannot guess one
// and must read it with get_plan_status after the person approves.
public static class ApprovalIds
{
    public static string New() => "apr_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
}
