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
        Assert.True(_model.HasBlocked);
        Assert.Equal("1 câu hỏi cho thiết kế (không ghi được)", _model.BlockedHeader);
    }

    [Fact]
    public void Rows_describe_the_element_and_the_change()
    {
        var row = _model.Items[0];

        Assert.Equal("Walls · Basic Wall (1)", row.Element);
        Assert.Equal("Comments: (trống) → Kiểm tra", row.Change);
        Assert.Equal(["1"], row.ElementIds);
        Assert.Equal("Cần xem", row.StatusText);
        Assert.Equal("Đặt tham số", row.ActionText);
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
        Assert.Equal((1, 0, 1), (_model.SelectedCount, _model.SkippedCount, _model.PendingCount));
        Assert.Equal("Chưa thực thi", _model.StatusBadge);
    }

    [Fact]
    public void Approve_all_skip_all_and_restore_act_on_the_main_list()
    {
        var sure = Item("E1", PlanTier.Review);
        sure.Confidence = 0.8;
        var document = new PlanDocument { Title = "DuAn", Path = @"D:\DuAn.rvt", StorageFolder = _folder };
        _model.Load(_plans.Create(document, "set_parameters", "Đặt Comments", scope: null, [sure, Item("E2", PlanTier.Review)]));

        _model.ApproveAll();
        Assert.All(_model.Items, row => Assert.Equal(PlanItemStatus.Approved, row.Status));
        Assert.True(_model.CanApprovePlan);

        // Only what needs a look: the item the system approved stays approved, folded away.
        Assert.False(ReviewViewModel.IsInMainList(_model.Items[0]));
        _model.RejectAll();
        Assert.Equal(PlanItemStatus.Approved, _model.Items[0].Status);
        Assert.Equal(PlanItemStatus.Rejected, _model.Items[1].Status);
        Assert.Equal("Bỏ qua", _model.Items[1].StatusText);

        _model.Toggle(_model.Items[0]);
        Assert.True(ReviewViewModel.IsInMainList(_model.Items[0]));
        _model.RestoreChoices();
        Assert.Equal(PlanItemStatus.Approved, _model.Items[0].Status);
        Assert.Equal("Tự duyệt", _model.Items[0].StatusText);
        Assert.Equal(PlanItemStatus.Pending, _model.Items[1].Status);
    }

    [Fact]
    public void The_tick_toggles_a_row_between_approved_and_skipped()
    {
        var row = _model.Items[0];

        _model.Toggle(row);
        Assert.Equal(PlanItemStatus.Approved, row.Status);
        _model.Toggle(row);
        Assert.Equal(PlanItemStatus.Rejected, row.Status);
    }

    [Fact]
    public void The_result_line_counts_elements_by_kind()
    {
        static PlanItem Columns(string id, int count) => new()
        {
            ItemId = id,
            Action = "create_columns",
            Params = JsonSerializer.SerializeToElement(new { summary = "350x1400" }),
            Source = JsonSerializer.SerializeToElement(new { name = id, category = "Structural Columns", count }),
            Confidence = 0.9,
        };
        var document = new PlanDocument { Title = "261016", Path = @"D:\261016.rvt", StorageFolder = _folder };
        var skipped = Columns("K-150x150-tangduoibo", 13);
        skipped.Confidence = 0.4;
        (skipped.Tier, skipped.Status, skipped.AutoStatus) = (PlanTier.Auto, PlanItemStatus.Rejected, PlanItemStatus.Rejected);
        var model = new ReviewViewModel(_plans, writesOnApprove: () => true);
        model.Load(_plans.Create(document, "create_columns", "Dựng cột", scope: null, [Columns("K-350x1400", 27), Columns("K-1000x1000", 18), skipped]));

        Assert.Equal("Tạo 45 cột (2 nhóm) trong mô hình Revit. Bỏ qua 1 nhóm còn lại.", model.ResultText);
        Assert.Equal("Duyệt và ghi (2)", model.PrimaryText);
        Assert.Equal("1 nhóm được hệ thống tự động bỏ qua", model.AutoSkippedHeader);
        Assert.False(ReviewViewModel.IsInMainList(model.Items[2]));
        Assert.Equal("Cần bạn xem (0)", model.ListHeader);
        Assert.Equal("2 nhóm được hệ thống tự động duyệt", model.AutoApprovedHeader);
        Assert.StartsWith(document.Title + " · pln_", model.Subtitle);
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
    public void A_change_type_row_selects_every_element_of_its_group()
    {
        var row = new PlanItemRow(new PlanItem
        {
            ItemId = "T-5476",
            Action = "change_type",
            Params = JsonSerializer.SerializeToElement(new { summary = "Đổi Type 3 phần tử", typeId = "357621", elementIds = new[] { "360447", "360448", "360449" } }),
            Source = JsonSerializer.SerializeToElement(new { name = "CW 102-85-100p → BTC-VachKC_300 (3)", category = "Walls", count = 3 }),
        });

        Assert.Equal(["360447", "360448", "360449"], row.ElementIds);
        Assert.Equal("Đổi Type phần tử", row.ActionText);
        Assert.Equal(3, row.ElementCount);
    }

    [Fact]
    public void Plan_can_be_approved_only_after_every_item_is_decided()
    {
        Assert.False(_model.CanApprovePlan);

        _model.SetStatus([_model.Items[0]], PlanItemStatus.Approved);
        Assert.False(_model.CanApprovePlan);

        Assert.Contains("Còn 1 mục cần xem", _model.ResultNote);
        _model.RejectRemaining();
        Assert.True(_model.CanApprovePlan);
        Assert.Equal(PlanItemStatus.Rejected, _model.Items[1].Status);

        _model.ApprovePlan();
        Assert.Equal(PlanStatus.Approved, _model.Plan!.Status);
        Assert.NotNull(_model.Plan.Approval);
        Assert.Contains("xong", _model.Message);
        Assert.Equal("Đã duyệt, chưa ghi", _model.StatusBadge);
    }

    [Fact]
    public void Rows_are_updated_in_place_so_the_selection_survives()
    {
        var row = _model.Items[0];

        _model.SetStatus([row], PlanItemStatus.Approved);

        Assert.Same(row, _model.Items[0]);
        Assert.Equal("Đã duyệt", row.StatusText);
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
        Assert.Equal("Đã huỷ", _model.StatusBadge);
    }

    [Fact]
    public void A_cancel_from_claude_reaches_the_view_model_through_changed()
    {
        _plans.Changed += plan => { if (plan.PlanId == _model.Plan?.PlanId) _model.Load(plan); };

        _plans.Cancel(_model.Plan!.Document, _model.Plan.PlanId);

        Assert.False(_model.CanEdit);
    }

    [Fact]
    public void Items_rejected_as_on_the_storey_below_are_hidden_and_counted()
    {
        var asBelow = Item("E1", PlanTier.Auto);
        asBelow.AutoStatus = asBelow.Status = PlanItemStatus.Rejected;
        var document = new PlanDocument { Title = "DuAn", Path = @"D:\DuAn.rvt", StorageFolder = _folder };
        _model.Load(_plans.Create(document, "set_parameters", "Đặt Comments", scope: null, [asBelow, Item("E2", PlanTier.Review)]));

        Assert.Equal("Tự bỏ", _model.Items[0].StatusText);
        Assert.False(ReviewViewModel.IsInMainList(_model.Items[0]));
        Assert.Equal(1, _model.AutoSkippedCount);

        // The reviewer may still approve it; it then shows again.
        _model.SetStatus([_model.Items[0]], PlanItemStatus.Approved);
        Assert.True(ReviewViewModel.IsInMainList(_model.Items[0]));
    }

    [Fact]
    public void Approving_hands_the_plan_to_the_commit_when_the_setting_asks()
    {
        var handed = new List<Plan>();
        var model = new ReviewViewModel(_plans, plan => { handed.Add(plan); return true; });
        model.Load(_model.Plan!);
        model.SetStatus(model.Items.ToList(), PlanItemStatus.Approved);

        model.ApprovePlan();

        var plan = Assert.Single(handed);
        Assert.Equal(PlanStatus.Approved, plan.Status);
        Assert.Equal("Đã duyệt. Đang ghi vào mô hình...", model.Message);
    }

    [Fact]
    public void Approving_without_the_commit_setting_sends_the_person_back_to_claude()
    {
        var model = new ReviewViewModel(_plans, _ => false);
        model.Load(_model.Plan!);
        model.SetStatus(model.Items.ToList(), PlanItemStatus.Approved);

        model.ApprovePlan();

        Assert.StartsWith("Đã duyệt kế hoạch. Quay lại Claude", model.Message);
    }

    [Fact]
    public void A_failed_approval_never_reaches_the_commit()
    {
        var handed = 0;
        var model = new ReviewViewModel(_plans, _ => { handed++; return true; });
        model.Load(_model.Plan!);

        model.ApprovePlan(); // items still pending

        Assert.Equal(0, handed);
        Assert.Contains("still need a decision", model.Message);
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
