using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using Bimtech.RevitMcp.Planning;
using Bimtech.RevitMcp.Protocol;

namespace Bimtech.RevitMcp.Ui;

// What the review window shows and what its buttons do. Uses no WPF or Revit types,
// so tests can press the "buttons" without Revit. Every action goes through PlanService,
// which saves the plan file; the window only redraws the result.
// commitApproved: asked after the person approves a plan; returns true when it started writing the plan into the model
// (setting CommitOnApprove), false when Claude should commit it. writesOnApprove: whether approving will write at once,
// for the wording of the main button.
public sealed class ReviewViewModel(PlanService plans, Func<Plan, bool>? commitApproved = null, Func<bool>? writesOnApprove = null)
    : INotifyPropertyChanged
{
    private string _message = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public Plan? Plan { get; private set; }
    public ObservableCollection<PlanItemRow> Items { get; } = [];
    // Blocked items cannot be approved; they become questions for the designer.
    public ObservableCollection<PlanItemRow> BlockedItems { get; } = [];

    public string Title => Plan?.Title ?? "";
    // The window's title bar names the plan, so a window left on an older plan is never mistaken for the new one.
    public string WindowTitle => Plan is null ? "BIMTECH · Duyệt kế hoạch" : $"BIMTECH · Duyệt kế hoạch {Plan.PlanId}";
    // e.g. "261016 · pln_20261018_31de12 · 25 nhóm phần tử".
    public string Subtitle => Plan is null ? "" : $"{Plan.Document.Title} · {Plan.PlanId} · {Items.Count + BlockedItems.Count} nhóm phần tử";

    // The badge at the top right.
    public string StatusBadge => Plan?.Status switch
    {
        PlanStatus.Committed => "Đã ghi",
        PlanStatus.Cancelled => "Đã huỷ",
        PlanStatus.Stale => "Đã cũ",
        PlanStatus.Approved => "Đã duyệt, chưa ghi",
        _ => "Chưa thực thi",
    };
    public string StatusKind => Plan?.Status switch
    {
        PlanStatus.Committed => "done",
        PlanStatus.Cancelled or PlanStatus.Stale => "off",
        _ => "open",
    };

    // The three cards: chosen to write (or written), skipped, still to look at.
    public int SelectedCount => Items.Count(row => row.Status is PlanItemStatus.Approved or PlanItemStatus.Applied);
    public int SkippedCount => Items.Count(row => row.Status == PlanItemStatus.Rejected);
    public int PendingCount => Items.Count(row => row.Status == PlanItemStatus.Pending);

    // The main list holds only what needs the person: items the system decided (approved by their confidence, or
    // skipped as on the storey below) wait folded away below it, and come back once the person changes them.
    public static bool IsInMainList(PlanItemRow row) => !row.IsDecidedForPerson;
    public static bool IsAutoApproved(PlanItemRow row) => row.IsDecidedForPerson && row.Status == PlanItemStatus.Approved;
    public static bool IsAutoSkipped(PlanItemRow row) => row.IsDecidedForPerson && row.Status == PlanItemStatus.Rejected;
    public int ToReviewCount => Items.Count(IsInMainList);
    public string ListHeader => $"Cần bạn xem ({ToReviewCount})";
    public int AutoApprovedCount => Items.Count(IsAutoApproved);
    public string AutoApprovedHeader => $"{AutoApprovedCount} nhóm được hệ thống tự động duyệt";
    public bool HasAutoApproved => AutoApprovedCount > 0;
    public int AutoSkippedCount => Items.Count(IsAutoSkipped);
    public string AutoSkippedHeader => $"{AutoSkippedCount} nhóm được hệ thống tự động bỏ qua";
    public bool HasAutoSkipped => AutoSkippedCount > 0;

    public string BlockedHeader => $"{BlockedItems.Count} câu hỏi cho thiết kế (không ghi được)";
    public bool HasBlocked => BlockedItems.Count > 0;

    // "Tạo 50 cột (4 nhóm) trong mô hình Revit. Bỏ qua 21 nhóm còn lại."
    public string ResultText
    {
        get
        {
            var chosen = Items.Where(row => row.Status is PlanItemStatus.Approved or PlanItemStatus.Applied).ToList();
            if (chosen.Count == 0) return "Chưa chọn mục nào để ghi.";
            var parts = chosen.GroupBy(row => (row.Verb, row.Noun)).Select(group =>
            {
                var count = group.Sum(row => row.ElementCount);
                return group.Count() == count
                    ? $"{group.Key.Verb} {count} {group.Key.Noun}"
                    : $"{group.Key.Verb} {count} {group.Key.Noun} ({group.Count()} nhóm)";
            });
            var done = Plan?.Status == PlanStatus.Committed ? "Đã ghi: " : "";
            var skipped = SkippedCount > 0 ? $" Bỏ qua {SkippedCount} nhóm còn lại." : "";
            return $"{done}{string.Join(", ", parts)} trong mô hình Revit.{skipped}";
        }
    }

    public string ResultNote => Plan?.Status switch
    {
        PlanStatus.Committed => "Hoàn tác cả lần ghi bằng một lần Ctrl+Z trong Revit.",
        _ when PendingCount > 0 => $"Còn {PendingCount} mục cần xem: chọn Duyệt hoặc Bỏ cho từng mục trước khi ghi.",
        _ when WritesOnApprove => "Ghi vào mô hình ngay khi bấm nút bên phải, là một bước Ctrl+Z.",
        _ => "Bấm duyệt rồi quay lại Claude để ghi vào mô hình.",
    };

    private bool WritesOnApprove => writesOnApprove?.Invoke() ?? false;
    public string PrimaryText => (WritesOnApprove ? "Duyệt và ghi" : "Duyệt kế hoạch") + $" ({SelectedCount})";

    // Why the plan can no longer be edited; empty while it can.
    public string ReadOnlyNotice => Plan?.Status switch
    {
        PlanStatus.Committed => "Kế hoạch này đã ghi vào mô hình, không sửa được nữa.",
        PlanStatus.Cancelled => "Kế hoạch này đã huỷ, không sửa được nữa.",
        PlanStatus.Stale => "Kế hoạch này đã cũ vì mô hình đổi sau khi đề xuất. Nhờ Claude đề xuất lại.",
        _ => "",
    };
    public bool CanEdit => Plan?.Status is PlanStatus.InReview or PlanStatus.Approved;
    public bool CanApprovePlan => Plan?.Status == PlanStatus.InReview
        && Items.All(row => row.Status != PlanItemStatus.Pending)
        && Items.Any(row => row.Status == PlanItemStatus.Approved);

    public string Message
    {
        get => _message;
        private set
        {
            _message = value;
            PropertyChanged?.Invoke(this, new(nameof(Message)));
        }
    }

    // Shows a plan. Reloading the same plan updates rows in place, so the
    // reviewer keeps their selection after pressing a button.
    public void Load(Plan plan)
    {
        var rows = Items.Concat(BlockedItems).ToDictionary(row => row.ItemId, StringComparer.Ordinal);
        var samePlan = Plan?.PlanId == plan.PlanId
            && rows.Count == plan.Items.Count
            && plan.Items.All(item => rows.ContainsKey(item.ItemId));
        Plan = plan;
        if (samePlan)
        {
            foreach (var item in plan.Items) rows[item.ItemId].Status = item.Status;
        }
        else
        {
            Items.Clear();
            BlockedItems.Clear();
            foreach (var item in plan.Items)
                (item.Tier == PlanTier.Blocked ? BlockedItems : Items).Add(new PlanItemRow(item));
            Message = "";
        }
        // An empty name tells WPF that every property may have changed.
        PropertyChanged?.Invoke(this, new(string.Empty));
    }

    public void SetStatus(IReadOnlyCollection<PlanItemRow> rows, PlanItemStatus status)
    {
        if (rows.Count == 0)
        {
            Message = "Chọn ít nhất một mục.";
            return;
        }
        Run(plan => plans.SetItemStatus(plan.Document, plan.PlanId, rows.Select(row => row.ItemId).ToList(), status), "");
    }

    // The tick in front of a row: approved becomes skipped, anything else approved.
    public void Toggle(PlanItemRow row) =>
        SetStatus([row], row.Status == PlanItemStatus.Approved ? PlanItemStatus.Rejected : PlanItemStatus.Approved);

    // "Duyệt tất cả" / "Bỏ duyệt tất cả" act on the main list; what the system decided stays as decided.
    public void ApproveAll() => SetAll(PlanItemStatus.Approved);

    public void RejectAll() => SetAll(PlanItemStatus.Rejected);

    private void SetAll(PlanItemStatus status)
    {
        var rows = Items.Where(row => IsInMainList(row) && row.Status != status
            && (row.Status is PlanItemStatus.Pending or PlanItemStatus.Approved or PlanItemStatus.Rejected)).ToList();
        if (rows.Count > 0) SetStatus(rows, status);
    }

    // "Khôi phục lựa chọn": every item back to what was proposed (decided for the person, or to look at).
    public void RestoreChoices()
    {
        foreach (var group in Items.Where(row => row.Status is PlanItemStatus.Pending or PlanItemStatus.Approved or PlanItemStatus.Rejected)
                     .GroupBy(row => row.AutoStatus ?? PlanItemStatus.Pending))
        {
            var changing = group.Where(row => row.Status != group.Key).ToList();
            if (changing.Count > 0 && !Run(plan => plans.SetItemStatus(plan.Document, plan.PlanId,
                    changing.Select(row => row.ItemId).ToList(), group.Key), "")) return;
        }
    }

    public void RejectRemaining() =>
        SetStatus(Items.Where(row => row.Status == PlanItemStatus.Pending).ToList(), PlanItemStatus.Rejected);

    public void ApprovePlan()
    {
        if (!Run(plan => plans.Approve(plan.Document, plan.PlanId), "") || Plan is null) return;
        Message = commitApproved?.Invoke(Plan) == true
            ? "Đã duyệt. Đang ghi vào mô hình..."
            : "Đã duyệt kế hoạch. Quay lại Claude và nói \"xong\" để tiếp tục.";
    }

    public void CancelPlan() => Run(plan => plans.Cancel(plan.Document, plan.PlanId),
        "Đã huỷ kế hoạch. Mô hình không thay đổi.");

    // For messages from outside the view model, e.g. when zooming to an element fails.
    public void ShowMessage(string message) => Message = message;

    // False when the action failed; its message is then shown.
    private bool Run(Func<Plan, Plan> action, string done)
    {
        if (Plan is null) return false;
        try
        {
            Load(action(Plan));
            Message = done;
            return true;
        }
        catch (Exception exception) when (exception is RpcException or InvalidOperationException
            or ArgumentException or IOException or UnauthorizedAccessException)
        {
            Message = exception.Message;
            return false;
        }
    }
}
