using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Bimtech.RevitMcp.DevTools;

// What a run_revit_query script sees. The script body runs as
//   public static object? Run(QueryContext ctx) { var doc = ctx.Doc; var uidoc = ctx.UiDoc; var view = ctx.View; <code> }
// so it may use doc, uidoc, view and ctx directly and should end with "return <value>;".
public sealed class QueryContext(UIApplication application, UIDocument uiDocument, CancellationToken cancellation)
{
    private const int MaxLogLines = 500;

    public UIApplication App { get; } = application;
    public UIDocument UiDoc { get; } = uiDocument;
    public Document Doc { get; } = uiDocument.Document;
    public View? View { get; } = uiDocument.Document.ActiveView;

    // Long loops should call ctx.Cancellation.ThrowIfCancellationRequested(): the client gave up waiting.
    public CancellationToken Cancellation { get; } = cancellation;

    // Lines written with Print, returned next to the result.
    public List<string> Log { get; } = [];

    public void Print(object? value)
    {
        if (Log.Count >= MaxLogLines) return;
        Log.Add(value switch
        {
            null => "null",
            string text => text,
            _ => TryJson(value) ?? value.ToString() ?? "",
        });
    }

    // Revit stores lengths in feet; the tools speak millimeters.
    public static double Mm(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);
    public static double Feet(double millimeters) => UnitUtils.ConvertToInternalUnits(millimeters, UnitTypeId.Millimeters);

    private static string? TryJson(object value)
    {
        try { return JsonSerializer.Serialize(value); }
        catch (Exception) { return null; }
    }
}
