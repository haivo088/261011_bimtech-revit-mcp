using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Bimtech.RevitMcp.Planning;

namespace Bimtech.RevitMcp.Ui;

// Selects and zooms to elements for the review window. Clicks in the window run
// outside Revit's API context, so they raise this external event instead of
// calling the API directly. Only the latest request matters, so older ones are dropped.
internal sealed class FocusHandler(Action<string> report) : IExternalEventHandler
{
    private readonly object _lock = new();
    private (PlanDocument Document, IReadOnlyList<string> ElementIds)? _request;

    public void Request(PlanDocument document, IReadOnlyList<string> elementIds)
    {
        lock (_lock) _request = (document, elementIds);
    }

    public void Execute(UIApplication application)
    {
        (PlanDocument Document, IReadOnlyList<string> ElementIds)? request;
        lock (_lock)
        {
            request = _request;
            _request = null;
        }
        if (request is null) return;
        var (document, elementIds) = request.Value;

        var uiDocument = application.ActiveUIDocument;
        if (uiDocument is null || !PlanStorageLocation.IsSameModel(uiDocument.Document, document))
        {
            report($"Mở mô hình \"{document.Title}\" để xem phần tử của kế hoạch này.");
            return;
        }
        var ids = elementIds
            .Select(text => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? new ElementId(value) : ElementId.InvalidElementId)
            .Where(id => id != ElementId.InvalidElementId && uiDocument.Document.GetElement(id) is not null)
            .ToList();
        if (ids.Count == 0)
        {
            report("Không còn phần tử này trong mô hình.");
            return;
        }
        try
        {
            uiDocument.Selection.SetElementIds(ids);
            uiDocument.ShowElements(ids);
            report("");
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException exception)
        {
            report($"Không zoom được tới phần tử: {exception.Message}");
        }
    }

    public string GetName() => "BIMTECH review window focus";
}
