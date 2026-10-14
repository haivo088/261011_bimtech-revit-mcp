using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;

namespace Bimtech.RevitMcp.Commands.Common;

// While active, Revit failures never open a dialog: nobody is at the keyboard to close it, so the
// bridge would hang until timeout. Warnings are recorded and dismissed; errors roll the transaction
// back and are recorded so the command can report them and undo the rest of its work.
// Use with "using" around every write: using var guard = new FailureGuard(context.UIApplication.Application);
public sealed class FailureGuard : IDisposable
{
    private const int MaxMessages = 20;
    private readonly Autodesk.Revit.ApplicationServices.Application _application;

    public FailureGuard(Autodesk.Revit.ApplicationServices.Application application)
    {
        _application = application;
        _application.FailuresProcessing += OnFailuresProcessing;
    }

    public List<string> Warnings { get; } = [];
    public int WarningCount { get; private set; }
    public List<string> Errors { get; } = [];

    private void OnFailuresProcessing(object? sender, FailuresProcessingEventArgs e)
    {
        var accessor = e.GetFailuresAccessor();
        var hasError = false;
        foreach (var failure in accessor.GetFailureMessages())
        {
            var text = failure.GetDescriptionText();
            if (failure.GetSeverity() == FailureSeverity.Warning)
            {
                WarningCount++;
                if (Warnings.Count < MaxMessages && !Warnings.Contains(text)) Warnings.Add(text);
                accessor.DeleteWarning(failure);
            }
            else
            {
                hasError = true;
                if (Errors.Count < MaxMessages) Errors.Add(text);
            }
        }
        if (hasError) e.SetProcessingResult(FailureProcessingResult.ProceedWithRollBack);
    }

    public void Dispose() => _application.FailuresProcessing -= OnFailuresProcessing;
}
