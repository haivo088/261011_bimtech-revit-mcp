using System.Text.Json;
using Autodesk.Revit.UI;
using Bimtech.RevitMcp.Commands;
using Bimtech.RevitMcp.Commands.Plans;
using Bimtech.RevitMcp.Planning;
using Bimtech.RevitMcp.Protocol;
using Bimtech.RevitMcp.Settings;

namespace Bimtech.RevitMcp.Ui;

// Writes a plan the person just approved in the review window (setting CommitOnApprove), so they need not go back to
// Claude. Clicks run outside Revit's API context, so the window raises this external event. It goes through the same
// commit_plan rules as Claude: the latest approval, a model unchanged since the proposal, read-only mode, commit_plan
// switched on. Claude's later commit_plan call then returns the same results without writing again.
internal sealed class CommitHandler(Func<CommitPlanCommand?> committer, Action<string> report) : IExternalEventHandler
{
    private readonly object _lock = new();
    private (PlanDocument Document, string PlanId, string ApprovalId)? _request;

    // Whether the window should commit this approved plan itself.
    public static bool Wanted(Plan plan) =>
        SettingsStore.Current.CommitOnApprove && plan.Approval is { Automatic: false } && plan.Status == PlanStatus.Approved;

    public void Request(Plan plan)
    {
        lock (_lock) _request = (plan.Document, plan.PlanId, plan.Approval!.ApprovalId);
    }

    public void Execute(UIApplication application)
    {
        (PlanDocument Document, string PlanId, string ApprovalId)? request;
        lock (_lock)
        {
            request = _request;
            _request = null;
        }
        if (request is null) return;
        var (document, planId, approvalId) = request.Value;

        if (committer() is not { } command)
        {
            report("Đã duyệt. Quay lại Claude và nói \"xong\" để ghi vào mô hình.");
            return;
        }
        var revitDocument = application.Application.Documents.Cast<Autodesk.Revit.DB.Document>()
            .FirstOrDefault(open => !open.IsLinked && PlanStorageLocation.IsSameModel(open, document));
        if (revitDocument is null)
        {
            report($"Đã duyệt, nhưng mô hình \"{document.Title}\" không còn mở nên chưa ghi.");
            return;
        }
        try
        {
            var parameters = new CommandParams(JsonSerializer.SerializeToElement(new { planId, approvalId }));
            CommandPolicy.EnsureAllowed(command, parameters, SettingsStore.Current);
            var result = command.Write(revitDocument, planId, approvalId);
            var applied = result.Plan.Items.Count(item => item.Status == PlanItemStatus.Applied);
            var failed = result.Plan.Items.Count(item => item.Status == PlanItemStatus.Failed);
            report(failed == 0
                ? $"Đã ghi {applied} mục vào mô hình. Ctrl+Z để hoàn tác cả lần ghi."
                : $"Đã ghi {applied} mục, {failed} mục lỗi (xem cột Lý do). Ctrl+Z để hoàn tác cả lần ghi.");
        }
        catch (RpcException exception) when (exception.Code == RpcErrorCodes.PlanStale)
        {
            report("Mô hình đã đổi sau khi đề xuất nên chưa ghi gì. Nhờ Claude đề xuất lại.");
        }
        catch (RpcException exception) when (exception.Code == RpcErrorCodes.CommandDisabled)
        {
            report("Đã duyệt, nhưng Settings đang chặn ghi (chế độ chỉ đọc hoặc tắt commit_plan) nên chưa ghi.");
        }
        catch (Exception exception) when (exception is RpcException or InvalidOperationException
            or Autodesk.Revit.Exceptions.ApplicationException)
        {
            report("Đã duyệt nhưng chưa ghi được: " + exception.Message);
        }
    }

    public string GetName() => "BIMTECH review window commit";
}
