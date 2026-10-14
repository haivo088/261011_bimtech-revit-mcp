using System.Text.Json;
using System.Text.Json.Nodes;
using Bimtech.RevitMcp.Planning;
using Bimtech.RevitMcp.Protocol;
using Xunit;

namespace Bimtech.RevitMcp.Planning.Tests;

// Rules of the review flow from docs/04-architecture.md, checked without Revit.
public sealed class PlanServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "bimtech-plan-tests", Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new();
    private readonly PlanService _plans;
    private readonly PlanDocument _document;

    public PlanServiceTests()
    {
        _plans = new PlanService(new JsonPlanStore(), _clock);
        _document = new PlanDocument { Title = "DuAn", Path = @"D:\DuAn.rvt", StorageFolder = _folder };
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void New_plan_is_in_review_without_approval()
    {
        var plan = CreatePlan();

        Assert.Equal(PlanStatus.InReview, plan.Status);
        Assert.Null(plan.Approval);
        Assert.Equal(Plan.CurrentSchemaVersion, _plans.Get(_document, plan.PlanId).SchemaVersion);
    }

    [Fact]
    public void Approve_records_approval_id_and_approved_items()
    {
        var plan = CreatePlan();
        _plans.SetItemStatus(_document, plan.PlanId, ["E1"], PlanItemStatus.Approved);
        _plans.SetItemStatus(_document, plan.PlanId, ["E2"], PlanItemStatus.Rejected);

        var approved = _plans.Approve(_document, plan.PlanId);

        Assert.Equal(PlanStatus.Approved, approved.Status);
        Assert.Matches("^apr_[0-9a-f]{16}$", approved.Approval!.ApprovalId);
        Assert.Equal(["E1"], approved.Approval.ApprovedItemIds);
        // Saved, not just returned.
        Assert.Equal(approved.Approval.ApprovalId, _plans.Get(_document, plan.PlanId).Approval!.ApprovalId);
    }

    [Fact]
    public void Approve_needs_every_reviewable_item_decided()
    {
        var plan = CreatePlan();
        _plans.SetItemStatus(_document, plan.PlanId, ["E1"], PlanItemStatus.Approved);

        var error = Assert.Throws<RpcException>(() => _plans.Approve(_document, plan.PlanId));

        Assert.Equal(RpcErrorCodes.PlanWrongStatus, error.Code);
        Assert.Contains("1 item(s)", error.Message);
        Assert.Equal(PlanStatus.InReview, _plans.Get(_document, plan.PlanId).Status);
    }

    [Fact]
    public void Blocked_items_do_not_need_a_decision_and_stay_out_of_the_approval()
    {
        var plan = CreatePlan(blocked: true);
        _plans.SetItemStatus(_document, plan.PlanId, ["E1", "E2"], PlanItemStatus.Approved);

        var approved = _plans.Approve(_document, plan.PlanId);

        Assert.Equal(["E1", "E2"], approved.Approval!.ApprovedItemIds);
    }

    [Fact]
    public void Blocked_items_cannot_be_approved()
    {
        var plan = CreatePlan(blocked: true);

        var error = Assert.Throws<RpcException>(
            () => _plans.SetItemStatus(_document, plan.PlanId, ["E1", "B3"], PlanItemStatus.Approved));

        Assert.Equal(RpcErrorCodes.PlanWrongStatus, error.Code);
        Assert.Contains("read-only", error.Message);
        // Nothing was saved, not even E1.
        Assert.All(_plans.Get(_document, plan.PlanId).Items, item => Assert.Equal(PlanItemStatus.Pending, item.Status));
    }

    [Fact]
    public void Approve_needs_at_least_one_approved_item()
    {
        var plan = CreatePlan();
        _plans.SetItemStatus(_document, plan.PlanId, ["E1", "E2"], PlanItemStatus.Rejected);

        var error = Assert.Throws<RpcException>(() => _plans.Approve(_document, plan.PlanId));

        Assert.Contains("No item is approved", error.Message);
    }

    [Fact]
    public void Editing_an_approved_plan_withdraws_the_approval()
    {
        var plan = ApprovedPlan();
        var firstApproval = plan.Approval!.ApprovalId;

        var edited = _plans.SetItemStatus(_document, plan.PlanId, ["E2"], PlanItemStatus.Rejected);

        Assert.Equal(PlanStatus.InReview, edited.Status);
        Assert.Null(edited.Approval);

        var again = _plans.Approve(_document, plan.PlanId);
        Assert.NotEqual(firstApproval, again.Approval!.ApprovalId);
    }

    [Fact]
    public void Repeating_a_decision_changes_nothing()
    {
        var plan = ApprovedPlan();
        var changes = 0;
        _plans.Changed += _ => changes++;
        _clock.Advance(TimeSpan.FromMinutes(1));

        var same = _plans.SetItemStatus(_document, plan.PlanId, ["E1"], PlanItemStatus.Approved);

        Assert.Equal(PlanStatus.Approved, same.Status);
        Assert.Equal(plan.Approval!.ApprovalId, same.Approval!.ApprovalId);
        Assert.Equal(plan.UpdatedAt, same.UpdatedAt);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Pending_undoes_a_decision()
    {
        var plan = CreatePlan();
        _plans.SetItemStatus(_document, plan.PlanId, ["E1"], PlanItemStatus.Rejected);

        var undone = _plans.SetItemStatus(_document, plan.PlanId, ["E1"], PlanItemStatus.Pending);

        Assert.Equal(PlanItemStatus.Pending, undone.Items.Single(item => item.ItemId == "E1").Status);
    }

    [Theory]
    [InlineData(PlanItemStatus.Applied)]
    [InlineData(PlanItemStatus.Failed)]
    public void Reviewers_cannot_set_commit_results(PlanItemStatus status)
    {
        var plan = CreatePlan();

        Assert.Throws<ArgumentOutOfRangeException>(() => _plans.SetItemStatus(_document, plan.PlanId, ["E1"], status));
    }

    [Fact]
    public void Unknown_items_are_rejected()
    {
        var plan = CreatePlan();

        var error = Assert.Throws<RpcException>(
            () => _plans.SetItemStatus(_document, plan.PlanId, ["E1", "E99"], PlanItemStatus.Approved));

        Assert.Equal(RpcErrorCodes.InvalidParams, error.Code);
        Assert.Contains("E99", error.Message);
    }

    [Fact]
    public void Move_cannot_approve()
    {
        var plan = CreatePlan();

        Assert.Throws<InvalidOperationException>(() => _plans.Move(_document, plan.PlanId, PlanStatus.Approved));
    }

    [Fact]
    public void Cancelling_clears_the_approval()
    {
        var plan = ApprovedPlan();

        var cancelled = _plans.Cancel(_document, plan.PlanId);

        Assert.Equal(PlanStatus.Cancelled, cancelled.Status);
        Assert.Null(cancelled.Approval);
    }

    [Fact]
    public void Cancelled_plans_cannot_be_edited()
    {
        var plan = CreatePlan();
        _plans.Cancel(_document, plan.PlanId);

        var error = Assert.Throws<RpcException>(
            () => _plans.SetItemStatus(_document, plan.PlanId, ["E1"], PlanItemStatus.Approved));

        Assert.Equal(RpcErrorCodes.PlanWrongStatus, error.Code);
    }

    [Fact]
    public void Changed_reports_each_saved_change()
    {
        var seen = new List<PlanStatus>();
        _plans.Changed += plan => seen.Add(plan.Status);

        var plan = CreatePlan();
        _plans.SetItemStatus(_document, plan.PlanId, ["E1", "E2"], PlanItemStatus.Approved);
        _plans.Approve(_document, plan.PlanId);
        _plans.Cancel(_document, plan.PlanId);

        Assert.Equal([PlanStatus.InReview, PlanStatus.InReview, PlanStatus.Approved, PlanStatus.Cancelled], seen);
    }

    [Fact]
    public void Items_hash_survives_saving_and_loading_vietnamese_text()
    {
        var plan = ApprovedPlan();

        var loaded = _plans.Get(_document, plan.PlanId);
        var approvedItems = loaded.Items.Where(item => loaded.Approval!.ApprovedItemIds.Contains(item.ItemId));

        Assert.Equal(plan.Approval!.ItemsHash, PlanApproval.HashItems(approvedItems));
    }

    [Fact]
    public void Items_hash_changes_when_a_value_changes()
    {
        var original = Item("E1", "Kiểm tra");
        var changed = Item("E1", "Kiểm tra lại");

        Assert.NotEqual(PlanApproval.HashItems([original]), PlanApproval.HashItems([changed]));
    }

    [Fact]
    public void Version_1_files_load_and_are_upgraded_on_save()
    {
        var plan = CreatePlan();
        var path = Path.Combine(_folder, plan.PlanId + ".json");
        // A version 1 file has neither the approval (version 2) nor the model stamp (version 3).
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["schemaVersion"] = 1;
        Assert.True(json.Remove("approval"));
        Assert.True(json.Remove("modelStamp"));
        File.WriteAllText(path, json.ToJsonString());

        var loaded = _plans.Get(_document, plan.PlanId);
        Assert.Null(loaded.Approval);
        Assert.Null(loaded.ModelStamp);

        _plans.SetItemStatus(_document, plan.PlanId, ["E1"], PlanItemStatus.Approved);
        Assert.Contains($"\"schemaVersion\": {Plan.CurrentSchemaVersion}", File.ReadAllText(path));
    }

    [Fact]
    public void Plan_file_keeps_vietnamese_readable()
    {
        var plan = CreatePlan();

        Assert.Contains("Kiểm tra", File.ReadAllText(Path.Combine(_folder, plan.PlanId + ".json")));
    }

    [Theory]
    [InlineData(@"..\pln_20261014_8f3a2c")]
    [InlineData("pln_20261014_8F3A2C")]
    [InlineData("")]
    public void Plan_ids_that_could_escape_the_folder_are_rejected(string planId)
    {
        var error = Assert.Throws<RpcException>(() => _plans.Get(_document, planId));

        Assert.Equal(RpcErrorCodes.InvalidParams, error.Code);
    }

    private Plan CreatePlan(bool blocked = false)
    {
        var items = new List<PlanItem> { Item("E1", "Kiểm tra"), Item("E2", "Kiểm tra") };
        if (blocked)
            items.Add(new PlanItem
            {
                ItemId = "B3",
                Action = "set_parameter",
                Params = JsonSerializer.SerializeToElement(new { elementId = "3" }),
                Tier = PlanTier.Blocked,
                Reasons = ["'Comments' is read-only."],
            });
        return _plans.Create(_document, "set_parameters", "Đặt Comments", scope: null, items);
    }

    private Plan ApprovedPlan()
    {
        var plan = CreatePlan();
        _plans.SetItemStatus(_document, plan.PlanId, ["E1", "E2"], PlanItemStatus.Approved);
        return _plans.Approve(_document, plan.PlanId);
    }

    private static PlanItem Item(string itemId, string value) => new()
    {
        ItemId = itemId,
        Action = "set_parameter",
        Params = JsonSerializer.SerializeToElement(new { elementId = itemId[1..], parameter = "Comments", value }),
    };

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
