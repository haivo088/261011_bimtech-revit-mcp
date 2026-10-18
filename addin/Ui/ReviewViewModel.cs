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
// (setting CommitOnApprove), false when Claude should commit it.
public sealed class ReviewViewModel(PlanService plans, Func<Plan, bool>? commitApproved = null) : INotifyPropertyChanged
{
    private string _message = "";
    private bool _showAutoItems;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Plan? Plan { get; private set; }
    public ObservableCollection<PlanItemRow> Items { get; } = [];
    // Blocked items cannot be approved; they become questions for the designer.
    public ObservableCollection<PlanItemRow> BlockedItems { get; } = [];

    public string Title => Plan?.Title ?? "";
    // The window's title bar names the plan, so a window left on an older plan is never mistaken for the new one.
    public string WindowTitle => Plan is null ? "BIMTECH · Duyệt kế hoạch" : $"BIMTECH · Duyệt kế hoạch {Plan.PlanId}";
    public string ItemsHeader
    {
        get
        {
            var approved = Items.Count(row => row.IsDecidedForPerson && row.Status == PlanItemStatus.Approved);
            var rejected = Items.Count(row => row.IsDecidedForPerson && row.Status == PlanItemStatus.Rejected);
            if (approved + rejected == 0) return $"Mục cần duyệt ({Items.Count})";
            return $"Cần xem ({Items.Count - approved - rejected})"
                + (approved > 0 ? $" · tự duyệt {approved}" : "") + (rejected > 0 ? $" · tự bỏ {rejected}" : "");
        }
    }

    // Items decided for the reviewer (approved by their confidence, or rejected as on the storey below) are hidden unless
    // the reviewer asks to see them: the list shows what needs a look.
    public bool ShowAutoItems
    {
        get => _showAutoItems;
        set
        {
            if (_showAutoItems == value) return;
            _showAutoItems = value;
            PropertyChanged?.Invoke(this, new(nameof(ShowAutoItems)));
        }
    }

    public bool IsVisible(PlanItemRow row) => ShowAutoItems || !row.IsDecidedForPerson;

    // Why the plan can no longer be edited; empty while it can.
    public string ReadOnlyNotice => Plan?.Status switch
    {
        PlanStatus.Committed => "Kế hoạch này đã ghi vào mô hình, không sửa được nữa.",
        PlanStatus.Cancelled => "Kế hoạch này đã huỷ, không sửa được nữa.",
        PlanStatus.Stale => "Kế hoạch này đã cũ vì mô hình đổi sau khi đề xuất. Nhờ Claude đề xuất lại.",
        _ => "",
    };
    public string BlockedHeader => $"Câu hỏi cho thiết kế ({BlockedItems.Count})";
    public bool CanEdit => Plan?.Status is PlanStatus.InReview or PlanStatus.Approved;
    public bool CanApprovePlan => Plan?.Status == PlanStatus.InReview
        && Items.All(row => row.Status != PlanItemStatus.Pending)
        && Items.Any(row => row.Status == PlanItemStatus.Approved);

    // e.g. "DuAn_KetCau · Đang duyệt · 3 duyệt (2 tự duyệt) · 1 bỏ · 2 chờ".
    public string StatusLine
    {
        get
        {
            if (Plan is null) return "";
            var autoApproved = Items.Count(row => row.IsDecidedForPerson && row.Status == PlanItemStatus.Approved);
            var autoRejected = Items.Count(row => row.IsDecidedForPerson && row.Status == PlanItemStatus.Rejected);
            return string.Join(" · ",
                Plan.Document.Title,
                StatusName(Plan.Status),
                $"{Count(PlanItemStatus.Approved)} duyệt" + (autoApproved > 0 ? $" ({autoApproved} tự duyệt)" : ""),
                $"{Count(PlanItemStatus.Rejected)} bỏ" + (autoRejected > 0 ? $" ({autoRejected} tự bỏ)" : ""),
                $"{Count(PlanItemStatus.Pending)} chờ");
        }
    }

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

    private int Count(PlanItemStatus status) => Items.Count(row => row.Status == status);

    private static string StatusName(PlanStatus status) => status switch
    {
        PlanStatus.InReview => "Đang duyệt",
        PlanStatus.Approved => "Đã duyệt",
        PlanStatus.Committed => "Đã ghi",
        PlanStatus.Stale => "Đã cũ, cần đề xuất lại",
        _ => "Đã huỷ",
    };
}
