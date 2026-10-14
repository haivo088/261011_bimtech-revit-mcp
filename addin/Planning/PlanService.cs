using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bimtech.RevitMcp.Protocol;

namespace Bimtech.RevitMcp.Planning;

// Creates plans, looks them up and moves them between statuses.
// Only the transitions in PlanTransitions are allowed, and only the review window
// (step 2) will move a plan to Approved; no JSON-RPC command can.
public sealed class PlanService(IPlanStore store, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    // Commands run on Revit's API thread, but the review window will run on the UI thread.
    private readonly object _lock = new();

    public Plan Create(PlanDocument document, string kind, string title, JsonElement? scope,
        IEnumerable<PlanItem> items, IEnumerable<PlanQuestion>? questions = null)
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
        };
        Validate(plan);
        lock (_lock) store.Save(plan);
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

    public Plan Move(PlanDocument document, string planId, PlanStatus target)
    {
        lock (_lock)
        {
            var plan = Get(document, planId);
            PlanTransitions.Ensure(plan, target);
            plan.Status = target;
            plan.UpdatedAt = _clock.GetUtcNow();
            store.Save(plan);
            return plan;
        }
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
