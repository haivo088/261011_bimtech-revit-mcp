using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bimtech.RevitMcp.Planning;

// A list of proposed model changes that a person reviews before anything is written.
// Plans are plain data with no Revit types, so they can be saved as JSON and tested
// without Revit. See docs/04-architecture.md for the full lifecycle.
public sealed class Plan
{
    public const int CurrentSchemaVersion = 1;

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
    // 0..1, or null until confidence rules exist. Every item is reviewed for now.
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

// Auto: may be written without per-item review. Review: needs a person. Blocked: never written.
[JsonConverter(typeof(JsonStringEnumConverter<PlanTier>))]
public enum PlanTier { Auto, Review, Blocked }

[JsonConverter(typeof(JsonStringEnumConverter<PlanQuestionStatus>))]
public enum PlanQuestionStatus { Open, Answered }
