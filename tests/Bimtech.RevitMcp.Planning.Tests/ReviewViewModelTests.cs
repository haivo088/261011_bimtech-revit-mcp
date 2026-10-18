using System.Text.Json;
using Bimtech.RevitMcp.Planning;
using Bimtech.RevitMcp.Ui;
using Xunit;

namespace Bimtech.RevitMcp.Planning.Tests;

// Presses the review window's buttons through its view model, without WPF or Revit.
public sealed class ReviewViewModelTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "bimtech-review-tests", Guid.NewGuid().ToString("N"));
    private readonly PlanService _plans = new(new JsonPlanStore());
    private readonly ReviewViewModel _model;

    public ReviewViewModelTests()
    {
        _model = new ReviewViewModel(_plans);
        var document = new PlanDocument { Title = "DuAn", Path = @"D:\DuAn.rvt", StorageFolder = _folder };
        _model.Load(_plans.Create(document, "set_parameters", "Đặt Comments", scope: null,
        [
            Item("E1", PlanTier.Review),
            Item("E2", PlanTier.Review),
            Item("E3", PlanTier.Blocked, "'Comments' is read-only."),
        ]));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void Blocked_items_get_their_own_tab()
    {
        Assert.Equal(["E1", "E2"], _model.Items.Select(row => row.ItemId));
        Assert.Equal(["E3"], _model.BlockedItems.Select(row => row.ItemId));
        Assert.Equal("Mục cần duyệt (2)", _model.ItemsHeader);
        Assert.Equal("Câu hỏi cho thiết kế (1)", _model.BlockedHeader);
    }

    [Fact]
    public void Rows_describe_the_element_and_the_change()
    {
        var row = _model.Items[0];

        Assert.Equal("Walls · Basic Wall (1)", row.Element);
        Assert.Equal("Comments: (trống) → Kiểm tra", row.Change);
        Assert.Equal(["1"], row.ElementIds);
        Assert.Equal("Chờ", row.StatusText);
    }

    [Fact]
    public void Automatic_approvals_show_their_confidence_and_are_counted()
    {
        var sure = Item("E1", PlanTier.Review);
        sure.Confidence = 0.954;
        var document = new PlanDocument { Title = "DuAn", Path = @"D:\DuAn.rvt", StorageFolder = _folder };
        _model.Load(_plans.Create(document, "set_parameters", "Đặt Comments", scope: null, [sure, Item("E2", PlanTier.Review)]));

        Assert.Equal("Tự duyệt", _model.Items[0].StatusText);
        Assert.Equal("95%", _model.Items[0].ConfidenceText);
        Assert.Equal("", _model.Items[1].ConfidenceText);
        Assert.Equal("DuAn · Đang duyệt · 1 duyệt (1 tự duyệt) · 0 bỏ · 1 chờ", _model.StatusLine);
        Assert.Equal("Cần xem (1) · tự duyệt 1", _model.ItemsHeader);
    }

    [Fact]
    public void The_list_hides_automatic_approvals_until_asked()
    {
        var sure = Item("E1", PlanTier.Review);
        sure.Confidence = 0.8;
        var document = new PlanDocument { Title = "DuAn", Path = @"D:\DuAn.rvt", StorageFolder = _folder };
        _model.Load(_plans.Create(document, "set_parameters", "Đặt Comments", scope: null, [sure, Item("E2", PlanTier.Review)]));

        Assert.False(_model.IsVisible(_model.Items[0]));
        Assert.True(_model.IsVisible(_model.Items[1]));
        _model.ShowAutoItems = true;
        Assert.True(_model.IsVisible(_model.Items[0]));
        // An automatic approval the reviewer undoes needs a look again.
        _model.ShowAutoItems = false;
        _model.SetStatus([_model.Items[0]], PlanItemStatus.Rejected);
        Assert.True(_model.IsVisible(_model.Items[0]));
    }

    [Fact]
    public void A_plan_that_can_no_longer_change_says_so()
    {
        Assert.Equal("", _model.ReadOnlyNotice);
        Assert.Contains(_model.Plan!.PlanId, _model.WindowTitle);

        _model.CancelPlan();

        Assert.Equal("Kế hoạch này đã huỷ, không sửa được nữa.", _model.ReadOnlyNotice);
    }

    [Fact]
    public void Rows_show_the_summary_of_items_that_have_one()
    {
        var row = new PlanItemRow(new PlanItem
        {
            ItemId = "G-B",
            Action = "create_grid",
            Params = JsonSerializer.SerializeToElement(new { summary = "Trục B (ngang): y = 6000 mm, dài 25 m", name = "B" }),
            Source = JsonSerializer.SerializeToElement(new { name = "B", category = "Grids" }),
        });

        Assert.Equal("Grids · B", row.Element);
        Assert.Equal("Trục B (ngang): y = 6000 mm, dài 25 m", row.Change);
        Assert.Empty(row.ElementIds);
    }

    [Fact]
    public void Plan_can_be_approved_only_after_every_item_is_decided()
    {
        Assert.False(_model.CanApprovePlan);

        _model.SetStatus([_model.Items[0]], PlanItemStatus.Approved);
        Assert.False(_model.CanApprovePlan);

        _model.RejectRemaining();
        Assert.True(_model.CanApprovePlan);
        Assert.Equal(PlanItemStatus.Rejected, _model.Items[1].Status);

        _model.ApprovePlan();
        Assert.Equal(PlanStatus.Approved, _model.Plan!.Status);
        Assert.NotNull(_model.Plan.Approval);
        Assert.Contains("xong", _model.Message);
        Assert.Contains("Đã duyệt", _model.StatusLine);
    }

    [Fact]
    public void Rows_are_updated_in_place_so_the_selection_survives()
    {
        var row = _model.Items[0];

        _model.SetStatus([row], PlanItemStatus.Approved);

        Assert.Same(row, _model.Items[0]);
        Assert.Equal("Duyệt", row.StatusText);
    }

    [Fact]
    public void Errors_appear_as_a_message_instead_of_crashing_the_window()
    {
        _model.ApprovePlan();

        Assert.Contains("still need a decision", _model.Message);
        Assert.Equal(PlanStatus.InReview, _model.Plan!.Status);
    }

    [Fact]
    public void Nothing_selected_asks_for_a_selection()
    {
        _model.SetStatus([], PlanItemStatus.Approved);

        Assert.Equal("Chọn ít nhất một mục.", _model.Message);
    }

    [Fact]
    public void Cancelling_disables_editing()
    {
        _model.CancelPlan();

        Assert.False(_model.CanEdit);
        Assert.False(_model.CanApprovePlan);
        Assert.Contains("Đã huỷ", _model.StatusLine);
    }

    [Fact]
    public void A_cancel_from_claude_reaches_the_view_model_through_changed()
    {
        _plans.Changed += plan => { if (plan.PlanId == _model.Plan?.PlanId) _model.Load(plan); };

        _plans.Cancel(_model.Plan!.Document, _model.Plan.PlanId);

        Assert.False(_model.CanEdit);
    }

    private static PlanItem Item(string itemId, PlanTier tier, params string[] reasons) => new()
    {
        ItemId = itemId,
        Action = "set_parameter",
        Params = JsonSerializer.SerializeToElement(new
        {
            elementId = itemId[1..],
            parameter = "Comments",
            value = "Kiểm tra",
            currentValue = (string?)null,
        }),
        Tier = tier,
        Reasons = [.. reasons],
        Source = JsonSerializer.SerializeToElement(new { elementId = itemId[1..], name = "Basic Wall", category = "Walls" }),
    };
}
