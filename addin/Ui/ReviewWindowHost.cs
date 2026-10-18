using System.Windows;
using System.Windows.Interop;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Bimtech.RevitMcp.Commands.Plans;
using Bimtech.RevitMcp.Planning;

namespace Bimtech.RevitMcp.Ui;

// Owns the single review window. Commands call Show from Revit's main thread;
// the window then lives on that thread, like Revit's own dialogs.
// Create it in a Revit API context, because it creates an ExternalEvent.
public sealed class ReviewWindowHost : IDisposable
{
    private readonly PlanService _plans;
    private readonly FocusHandler _focus;
    private readonly ExternalEvent _focusEvent;
    private readonly CommitHandler _commit;
    private readonly ExternalEvent _commitEvent;
    private Autodesk.Revit.ApplicationServices.Application? _application;
    private ReviewWindow? _window;
    private ReviewViewModel? _model;

    public ReviewWindowHost(PlanService plans)
    {
        _plans = plans;
        _focus = new FocusHandler(message => _model?.ShowMessage(message));
        _focusEvent = ExternalEvent.Create(_focus);
        _commit = new CommitHandler(() => Committer, message => _model?.ShowMessage(message));
        _commitEvent = ExternalEvent.Create(_commit);
        _plans.Changed += OnPlanChanged;
    }

    // commit_plan of the current command registry, set by App.CreateRegistry: the review window writes an approved plan
    // with it when the person turned on CommitOnApprove.
    public CommitPlanCommand? Committer { get; set; }

    // Opens the window, or brings it forward with this plan. Returns at once;
    // the person reviews while Claude and Revit carry on.
    // A plan the auto-commit setting already approved has nothing to review: no window. open_plan_review still shows it.
    public void Show(Plan plan, UIApplication application, bool evenIfAutoApproved = false)
    {
        if (plan.Approval?.Automatic == true && !evenIfAutoApproved) return;
        if (_application is null)
        {
            _application = application.Application;
            _application.DocumentClosing += OnDocumentClosing;
        }
        if (_window is null)
        {
            _model = new ReviewViewModel(_plans, CommitApproved, () => Committer is not null && Settings.SettingsStore.Current.CommitOnApprove);
            _window = new ReviewWindow(_model, FocusElements);
            // Keeps the window above Revit and minimizes it together with Revit.
            _ = new WindowInteropHelper(_window) { Owner = application.MainWindowHandle };
            _window.Closed += (_, _) =>
            {
                (_window, _model) = (null, null);
                // Nothing the window drew stays in the views.
                _focus.RequestClear();
                _focusEvent.Raise();
            };
        }
        _model!.Load(plan);
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    // Asked by the window right after the person approves; the commit itself runs in the external event.
    private bool CommitApproved(Plan plan)
    {
        if (Committer is null || !CommitHandler.Wanted(plan)) return false;
        _commit.Request(plan);
        _commitEvent.Raise();
        return true;
    }

    private void FocusElements(IReadOnlyList<string> elementIds, IReadOnlyList<PreviewGroup> previews)
    {
        if (_model?.Plan is not { } plan) return;
        _focus.Request(plan.Document, elementIds, previews);
        _focusEvent.Raise();
    }

    // Keeps the window in step when Claude cancels the plan it shows.
    private void OnPlanChanged(Plan plan)
    {
        if (_window is null || _model?.Plan?.PlanId != plan.PlanId) return;
        if (_window.Dispatcher.CheckAccess()) _model.Load(plan);
        else _window.Dispatcher.Invoke(() => _model?.Load(plan));
    }

    // Plans belong to one model, so the window closes with it. The plan itself stays.
    private void OnDocumentClosing(object? sender, DocumentClosingEventArgs e)
    {
        _focus.ClearPreviewOf(e.Document);
        if (_model?.Plan is { } plan && PlanStorageLocation.IsSameModel(e.Document, plan.Document))
            _window?.Close();
    }

    public void Dispose()
    {
        _plans.Changed -= OnPlanChanged;
        if (_application is not null) _application.DocumentClosing -= OnDocumentClosing;
        _window?.Close();
        _focus.Unregister();
        _focusEvent.Dispose();
        _commitEvent.Dispose();
    }
}
