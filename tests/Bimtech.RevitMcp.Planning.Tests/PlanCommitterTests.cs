using System.Text.Json;
using System.Text.Json.Nodes;
using Bimtech.RevitMcp.Planning;
using Bimtech.RevitMcp.Protocol;
using Xunit;

namespace Bimtech.RevitMcp.Planning.Tests;

// commit_plan rules (docs/04-architecture.md, principles 2–4), checked with a fake writer instead of Revit.
public sealed class PlanCommitterTests : IDisposable
{
    private static readonly ModelStamp Proposed = new("session-1", 418);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "bimtech-commit-tests", Guid.NewGuid().ToString("N"));
    private readonly JsonPlanStore _store = new();
    private readonly PlanService _plans;
    private readonly PlanCommitter _committer;
    private readonly PlanDocument _document;
    private readonly FakeWriter _writer = new();

    public PlanCommitterTests()
    {
        _plans = new PlanService(_store);
        _committer = new PlanCommitter(_plans, _store);
        _document = new PlanDocument { Title = "DuAn", Path = @"D:\DuAn.rvt", StorageFolder = _folder };
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void Commit_writes_only_approved_items_in_approval_order_and_records_results()
    {
        var plan = Approved(approve: ["E1", "E3"], reject: ["E2"]);
        _writer.FailItem = "E3";

        var result = _committer.Commit(_document, plan.PlanId, plan.Approval!.ApprovalId, Proposed, _writer);

        Assert.False(result.AlreadyCommitted);
        Assert.Equal(["E1", "E3"], _writer.Received);
        var saved = _plans.Get(_document, plan.PlanId);
        Assert.Equal(PlanStatus.Committed, saved.Status);
        Assert.NotNull(saved.Approval); // kept as a record of what was approved
        Assert.Equal(PlanItemStatus.Applied, Item(saved, "E1").Status);
        Assert.Equal(PlanItemStatus.Rejected, Item(saved, "E2").Status);
        Assert.Equal(PlanItemStatus.Failed, Item(saved, "E3").Status);
        Assert.Equal("boom", Item(saved, "E3").Result!.Value.GetProperty("error").GetString());
    }

    [Fact]
    public void Second_commit_returns_stored_results_without_writing_again()
    {
        var plan = Approved(approve: ["E1", "E2", "E3"]);
        var approvalId = plan.Approval!.ApprovalId;
        _committer.Commit(_document, plan.PlanId, approvalId, Proposed, _writer);
        _writer.Received.Clear();

        // Even if the model changed since, a committed plan just reports what it wrote.
        var again = _committer.Commit(_document, plan.PlanId, approvalId, Proposed with { ChangeCounter = 999 }, _writer);

        Assert.True(again.AlreadyCommitted);
        Assert.Empty(_writer.Received);
        Assert.Equal(PlanStatus.Committed, again.Plan.Status);
    }

    [Fact]
    public void Commit_needs_an_approved_plan()
    {
        var plan = _plans.Create(_document, "set_parameters", "Đặt Comments", null, Items(), modelStamp: Proposed);

        var error = Assert.Throws<RpcException>(() => _committer.Commit(_document, plan.PlanId, "apr_0000000000000000", Proposed, _writer));

        Assert.Equal(RpcErrorCodes.PlanWrongStatus, error.Code);
        Assert.Empty(_writer.Received);
    }

    [Fact]
    public void Commit_rejects_an_approval_id_that_is_not_the_latest()
    {
        var plan = Approved(approve: ["E1", "E2", "E3"]);

        var error = Assert.Throws<RpcException>(() => _committer.Commit(_document, plan.PlanId, "apr_0000000000000000", Proposed, _writer));

        Assert.Equal(RpcErrorCodes.ApprovalMismatch, error.Code);
        Assert.Equal(PlanStatus.Approved, _plans.Get(_document, plan.PlanId).Status);
        Assert.Empty(_writer.Received);
    }

    [Fact]
    public void Commit_refuses_items_edited_in_the_file_after_approval()
    {
        var plan = Approved(approve: ["E1", "E2", "E3"]);
        var path = Path.Combine(_folder, plan.PlanId + ".json");
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["items"]![0]!["params"]!["value"] = "giá trị khác";
        File.WriteAllText(path, json.ToJsonString());

        var error = Assert.Throws<RpcException>(() => _committer.Commit(_document, plan.PlanId, plan.Approval!.ApprovalId, Proposed, _writer));

        Assert.Equal(RpcErrorCodes.ApprovalMismatch, error.Code);
        Assert.Empty(_writer.Received);
    }

    [Theory]
    [InlineData("session-1", 419)] // the model changed
    [InlineData("session-2", 418)] // Revit restarted or the model was reopened
    public void Commit_marks_the_plan_stale_when_the_model_changed(string session, long counter)
    {
        var plan = Approved(approve: ["E1", "E2", "E3"]);

        var error = Assert.Throws<RpcException>(() =>
            _committer.Commit(_document, plan.PlanId, plan.Approval!.ApprovalId, new ModelStamp(session, counter), _writer));

        Assert.Equal(RpcErrorCodes.PlanStale, error.Code);
        Assert.Empty(_writer.Received);
        var saved = _plans.Get(_document, plan.PlanId);
        Assert.Equal(PlanStatus.Stale, saved.Status);
        Assert.Null(saved.Approval);

        // Stale is final: a later commit still refuses, with the same code.
        var again = Assert.Throws<RpcException>(() => _committer.Commit(_document, plan.PlanId, "apr_0000000000000000", Proposed, _writer));
        Assert.Equal(RpcErrorCodes.PlanStale, again.Code);
    }

    [Fact]
    public void Plan_without_a_model_stamp_is_treated_as_stale()
    {
        var plan = _plans.Create(_document, "set_parameters", "Đặt Comments", null, Items());
        _plans.SetItemStatus(_document, plan.PlanId, ["E1", "E2", "E3"], PlanItemStatus.Approved);
        plan = _plans.Approve(_document, plan.PlanId);

        var error = Assert.Throws<RpcException>(() => _committer.Commit(_document, plan.PlanId, plan.Approval!.ApprovalId, Proposed, _writer));

        Assert.Equal(RpcErrorCodes.PlanStale, error.Code);
    }

    [Fact]
    public void Commit_appends_one_audit_line()
    {
        var plan = Approved(approve: ["E1", "E2"], reject: ["E3"]);
        _writer.FailItem = "E2";

        _committer.Commit(_document, plan.PlanId, plan.Approval!.ApprovalId, Proposed, _writer);
        _committer.Commit(_document, plan.PlanId, plan.Approval.ApprovalId, Proposed, _writer); // already committed: no new line

        var lines = File.ReadAllLines(Path.Combine(_folder, JsonPlanStore.AuditFileName));
        var entry = Assert.Single(lines);
        var audit = JsonDocument.Parse(entry).RootElement;
        Assert.Equal(plan.PlanId, audit.GetProperty("planId").GetString());
        Assert.Equal(plan.Approval.ApprovalId, audit.GetProperty("approvalId").GetString());
        Assert.Equal(1, audit.GetProperty("applied").GetInt32());
        Assert.Equal(1, audit.GetProperty("failed").GetInt32());
    }

    [Fact]
    public void Effective_params_merge_reviewer_overrides_over_proposed_params()
    {
        var item = new PlanItem
        {
            ItemId = "E1",
            Action = "set_parameter",
            Params = JsonSerializer.SerializeToElement(new { elementId = "1", parameter = "Comments", value = "A" }),
            Overrides = JsonSerializer.SerializeToElement(new { value = "B" }),
        };

        var effective = PlanItemParams.Effective(item);

        Assert.Equal("B", effective.GetProperty("value").GetString());
        Assert.Equal("Comments", effective.GetProperty("parameter").GetString());
        Assert.Equal("A", item.Params.GetProperty("value").GetString()); // the proposal itself is unchanged
    }

    private Plan Approved(string[] approve, string[]? reject = null)
    {
        var plan = _plans.Create(_document, "set_parameters", "Đặt Comments", null, Items(), modelStamp: Proposed);
        _plans.SetItemStatus(_document, plan.PlanId, approve, PlanItemStatus.Approved);
        if (reject is not null) _plans.SetItemStatus(_document, plan.PlanId, reject, PlanItemStatus.Rejected);
        return _plans.Approve(_document, plan.PlanId);
    }

    private static List<PlanItem> Items() =>
    [
        Item("E1", "A"),
        Item("E2", "B"),
        Item("E3", "C"),
    ];

    private static PlanItem Item(string itemId, string value) => new()
    {
        ItemId = itemId,
        Action = "set_parameter",
        Params = JsonSerializer.SerializeToElement(new { elementId = itemId[1..], parameter = "Comments", value }),
    };

    private static PlanItem Item(Plan plan, string itemId) => plan.Items.Single(item => item.ItemId == itemId);

    // Records which items it was asked to write; fails the one named in FailItem.
    private sealed class FakeWriter : IPlanItemWriter
    {
        public List<string> Received { get; } = [];
        public string? FailItem { get; set; }

        public IReadOnlyList<PlanItemOutcome> Apply(IReadOnlyList<PlanItem> items)
        {
            Received.AddRange(items.Select(item => item.ItemId));
            return items.Select(item => item.ItemId == FailItem
                    ? new PlanItemOutcome(item.ItemId, false, JsonSerializer.SerializeToElement(new { error = "boom" }))
                    : new PlanItemOutcome(item.ItemId, true, JsonSerializer.SerializeToElement(new { value = "ok" })))
                .ToList();
        }
    }
}
