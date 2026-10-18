using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Bimtech.RevitMcp.Planning;

namespace Bimtech.RevitMcp.Ui;

// Shows the selected review rows in Revit: selects and zooms to elements that exist, and draws where new ones will be
// (PreviewServer) and zooms there. Clicks in the window run outside Revit's API context, so they raise this external
// event instead of calling the API directly. Only the latest request matters, so older ones are dropped.
// Never writes to the model.
internal sealed class FocusHandler(Action<string> report) : IExternalEventHandler
{
    // A lone column or pile is zoomed to with at least this much around it.
    private const double MinZoomSizeMm = 6000;

    private readonly object _lock = new();
    private readonly PreviewServer _preview = new();
    // A null document clears the preview.
    private (PlanDocument? Document, IReadOnlyList<string> ElementIds, IReadOnlyList<PreviewGroup> Previews)? _request;

    public void Request(PlanDocument document, IReadOnlyList<string> elementIds, IReadOnlyList<PreviewGroup> previews)
    {
        lock (_lock) _request = (document, elementIds, previews);
    }

    // Removes the preview, e.g. when the window closes.
    public void RequestClear()
    {
        lock (_lock) _request = (null, [], []);
    }

    // For DocumentClosing, which runs in an API context: drops the preview of that model at once.
    public void ClearPreviewOf(Autodesk.Revit.DB.Document document)
    {
        if (_preview.Shows(document)) _preview.Clear();
    }

    // Must run in a Revit API context, e.g. OnShutdown.
    public void Unregister() => _preview.Unregister();

    public void Execute(UIApplication application)
    {
        (PlanDocument? Document, IReadOnlyList<string> ElementIds, IReadOnlyList<PreviewGroup> Previews)? request;
        lock (_lock)
        {
            request = _request;
            _request = null;
        }
        if (request is null) return;
        var (document, elementIds, previews) = request.Value;

        var uiDocument = application.ActiveUIDocument;
        if (document is null || uiDocument is null || !PlanStorageLocation.IsSameModel(uiDocument.Document, document))
        {
            ClearPreview(uiDocument);
            if (document is not null) report($"Mở mô hình \"{document.Title}\" để xem phần tử của kế hoạch này.");
            return;
        }

        var lines = Polylines(uiDocument.Document, previews);
        _preview.Show(uiDocument.Document, lines);
        uiDocument.RefreshActiveView();

        if (elementIds.Count > 0) ShowElements(uiDocument, elementIds);
        else if (lines.Count > 0) ZoomTo(uiDocument, lines);
        else report("");
    }

    public string GetName() => "BIMTECH review window focus";

    private void ShowElements(UIDocument uiDocument, IReadOnlyList<string> elementIds)
    {
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

    // Zooms the active view to the preview, with some room around it.
    private void ZoomTo(UIDocument uiDocument, IReadOnlyList<PreviewServer.Polyline> lines)
    {
        var view = uiDocument.ActiveView;
        if (view is not (ViewPlan or View3D))
        {
            report("Mở một mặt bằng hoặc khung nhìn 3D để xem vị trí các mục sẽ tạo.");
            return;
        }
        var points = lines.SelectMany(line => line.Points).ToList();
        var center = new XYZ((points.Min(p => p.X) + points.Max(p => p.X)) / 2, (points.Min(p => p.Y) + points.Max(p => p.Y)) / 2,
            (points.Min(p => p.Z) + points.Max(p => p.Z)) / 2);
        var minHalf = Feet(MinZoomSizeMm) / 2;
        // A fifth of the size on each side, so the edges of the preview are not at the window border.
        var halfX = Math.Max(minHalf, (points.Max(p => p.X) - points.Min(p => p.X)) * 0.7);
        var halfY = Math.Max(minHalf, (points.Max(p => p.Y) - points.Min(p => p.Y)) * 0.7);
        try
        {
            var uiView = uiDocument.GetOpenUIViews().FirstOrDefault(open => open.ViewId == view.Id);
            uiView?.ZoomAndCenterRectangle(center - new XYZ(halfX, halfY, 0), center + new XYZ(halfX, halfY, 0));
            report("");
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException exception)
        {
            report($"Không zoom được tới vị trí mục: {exception.Message}");
        }
    }

    private void ClearPreview(UIDocument? uiDocument)
    {
        if (!_preview.HasLines) return;
        _preview.Clear();
        uiDocument?.RefreshActiveView();
    }

    // Plan items are in mm; the drawing is in feet, each item at its level (0 when the level does not exist yet).
    private static List<PreviewServer.Polyline> Polylines(Autodesk.Revit.DB.Document document, IReadOnlyList<PreviewGroup> previews)
    {
        var lines = new List<PreviewServer.Polyline>();
        foreach (var group in previews)
        {
            var z = group.LevelId is { } text
                && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                && document.GetElement(new ElementId(id)) is Level level
                ? level.ProjectElevation
                : 0;
            lines.AddRange(group.Shapes.Select(shape => new PreviewServer.Polyline(
                shape.Points.Select(point => new XYZ(Feet(point.X), Feet(point.Y), z)).ToList(), shape.Closed)));
        }
        return lines;
    }

    private static double Feet(double millimetres) => UnitUtils.ConvertToInternalUnits(millimetres, UnitTypeId.Millimeters);
}
