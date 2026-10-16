using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Bimtech.RevitMcp.DevTools;

// What a send_code snippet sees. The snippet body runs as
//   public static object? Run(QueryContext ctx) { var doc = ctx.Doc; var uidoc = ctx.UiDoc; var view = ctx.View; <code> }
// so it may use doc, uidoc, view and ctx directly and should end with "return <value>;".
// When it runs from commit_plan (write=true, approved) there is no UI: uidoc and App are null.
public sealed class QueryContext(Document document, UIApplication? application, UIDocument? uiDocument, CancellationToken cancellation, JsonElement? args = null)
{
    private const int MaxLogLines = 500;

    public QueryContext(UIApplication application, UIDocument uiDocument, CancellationToken cancellation, JsonElement? args = null)
        : this(uiDocument.Document, application, uiDocument, cancellation, args) { }

    public UIApplication? App { get; } = application;
    public UIDocument? UiDoc { get; } = uiDocument;
    public Document Doc { get; } = document;
    public View? View { get; } = document.ActiveView;

    // Long loops should call ctx.Cancellation.ThrowIfCancellationRequested(): the client gave up waiting.
    public CancellationToken Cancellation { get; } = cancellation;

    // The arguments of a command-set script (or of send_code's "args"), already checked against its parameters.
    public JsonElement Args { get; } = args is { ValueKind: JsonValueKind.Object } value ? value : JsonDocument.Parse("{}").RootElement;

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

    public bool Has(string name) => Args.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null;
    public string Str(string name) => Arg(name).GetString() ?? "";
    public double Num(string name) => Arg(name).GetDouble();
    public bool Bool(string name) => Arg(name).GetBoolean();
    public ElementId Id(string name) => ToId(Arg(name));
    public List<string> Strs(string name) => Arg(name).EnumerateArray().Select(v => v.GetString() ?? "").ToList();
    public List<ElementId> Ids(string name) => Arg(name).EnumerateArray().Select(ToId).ToList();

    // Revit stores lengths in feet; the tools speak millimeters.
    public static double Mm(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);
    public static double Feet(double millimeters) => UnitUtils.ConvertToInternalUnits(millimeters, UnitTypeId.Millimeters);

    private JsonElement Arg(string name) =>
        Args.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value
            : throw new ArgumentException($"Missing argument \"{name}\".");

    private static ElementId ToId(JsonElement value) =>
        new(value.ValueKind == JsonValueKind.Number ? value.GetInt64() : long.Parse(value.GetString()!, CultureInfo.InvariantCulture));

    private static string? TryJson(object value)
    {
        try { return JsonSerializer.Serialize(value); }
        catch (Exception) { return null; }
    }
}
