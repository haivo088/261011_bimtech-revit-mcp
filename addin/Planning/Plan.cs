using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bimtech.RevitMcp.Planning;

// A list of proposed model changes that a person reviews before anything is written.
// Plans are plain data with no Revit types, so they can be saved as JSON and tested
// without Revit. See docs/04-architecture.md for the full lifecycle.
public sealed class Plan
{
    // 2 added Approval; 3 added ModelStamp. Older files still load; the new fields are then null.
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string PlanId { get; set; } = "";
    // Which propose_* command made the plan, e.g. "set_parameters".
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public PlanDocument Document { get; set; } = new();
    // What the proposal covered, as sent by the propose command. Shown to the reviewer.
    public JsonElement? Scope { get; set; }
    public PlanStatus Status { get; set; } = PlanStatus.InReview;
    public List<PlanItem> Items { get; set; } = [];
    public List<PlanQuestion> Questions { get; set; } = [];
    // Set only by the review window when the person approves; cleared when they edit again.
    public PlanApproval? Approval { get; set; }
    // The model's change count when the plan was proposed. commit_plan refuses to write when the
    // model changed since (the plan is then Stale), because the items were checked against that state.
    public ModelStamp? ModelStamp { get; set; }

    public PlanSummary Summarize() => new(
        Items.Count,
        Items.Count(item => item.Status == PlanItemStatus.Pending),
        Items.Count(item => item.Status == PlanItemStatus.Approved),
        Items.Count(item => item.Status == PlanItemStatus.Rejected),
        Items.Count(item => item.Tier == PlanTier.Blocked),
        Questions.Count(question => question.Status == PlanQuestionStatus.Open));
}

// One proposed change, e.g. "set Comments of element 12345 to 'A'".
public sealed class PlanItem
{
    public string ItemId { get; set; } = "";
    // What the executor will do, e.g. "set_parameter". Each action has its own params shape.
    public string Action { get; set; } = "";
    public JsonElement Params { get; set; }
    // 0..1, or null when the command has no confidence rule. From PlanConfidence.AutoApproveFrom up, the item
    // starts approved (Tier Auto); the person still approves the plan and may undo the item's approval.
    public double? Confidence { get; set; }
    public PlanTier Tier { get; set; } = PlanTier.Review;
    // Plain-language reasons the reviewer and Claude can read, e.g. why an item is blocked.
    public List<string> Reasons { get; set; } = [];
    // Where the item came from, e.g. a CAD layer or a selected element.
    public JsonElement? Source { get; set; }
    public PlanItemStatus Status { get; set; } = PlanItemStatus.Pending;
    // Values the reviewer changed; merged over Params at commit time.
    public JsonElement? Overrides { get; set; }
    // Filled after commit: created element IDs or the error message.
    public JsonElement? Result { get; set; }
}

// What the person approved. commit_plan must send ApprovalId, and the executor (step 3)
// recomputes ItemsHash to make sure the items are still exactly the ones approved.
public sealed class PlanApproval
{
    public string ApprovalId { get; set; } = "";
    public DateTimeOffset ApprovedAt { get; set; }
    public List<string> ApprovedItemIds { get; set; } = [];
    public string ItemsHash { get; set; } = "";
    // Approved by the person's auto-commit setting (every item sure) rather than in the review window.
    public bool Automatic { get; set; }

    // SHA-256 over each item's ID, action, params and overrides, in the order given.
    public static string HashItems(IEnumerable<PlanItem> items)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var item in items)
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(new { item.ItemId, item.Action, item.Params, item.Overrides }));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}

// Identifies one state of an open model: the Revit session and how many DocumentChanged events the
// add-in has seen for that model. Any edit, undo or other add-in's change moves the counter.
public sealed record ModelStamp(string SessionId, long ChangeCounter);

// A question for the designer, raised when the drawings cannot settle an item.
public sealed class PlanQuestion
{
    public string QuestionId { get; set; } = "";
    public List<string> ItemIds { get; set; } = [];
    public string Text { get; set; } = "";
    public PlanQuestionStatus Status { get; set; } = PlanQuestionStatus.Open;
    public string? Answer { get; set; }
}

// Which model the plan belongs to. StorageFolder is where the plan file lives.
public sealed class PlanDocument
{
    public string Title { get; set; } = "";
    public string Path { get; set; } = "";
    [JsonIgnore] public string StorageFolder { get; set; } = "";
}

public sealed record PlanSummary(int Total, int Pending, int Approved, int Rejected, int Blocked, int OpenQuestions);

[JsonConverter(typeof(JsonStringEnumConverter<PlanStatus>))]
public enum PlanStatus { InReview, Approved, Committed, Stale, Cancelled }

[JsonConverter(typeof(JsonStringEnumConverter<PlanItemStatus>))]
public enum PlanItemStatus { Pending, Approved, Rejected, Applied, Failed }

// Auto: sure enough to start approved; still written only after the person approves the plan.
// Review: needs a person's decision. Blocked: never written.
[JsonConverter(typeof(JsonStringEnumConverter<PlanTier>))]
public enum PlanTier { Auto, Review, Blocked }

public static class PlanConfidence
{
    // Items at least this sure start approved: the Evidence scale starts here (no evidence either way), so an item is
    // approved unless something speaks against it. Recognition::Evidence.AutoFromPercent is the same threshold.
    public const double AutoApproveFrom = 0.5;

    // Turns sure Review items into pre-approved Auto items. Blocked items stay blocked whatever their confidence.
    public static void Apply(IEnumerable<PlanItem> items)
    {
        foreach (var item in items)
        {
            if (item.Tier != PlanTier.Review || item.Status != PlanItemStatus.Pending) continue;
            if (item.Confidence is not { } confidence || confidence < AutoApproveFrom) continue;
            item.Tier = PlanTier.Auto;
            item.Status = PlanItemStatus.Approved;
        }
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<PlanQuestionStatus>))]
public enum PlanQuestionStatus { Open, Answered }
