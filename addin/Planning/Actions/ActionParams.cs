using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace Bimtech.RevitMcp.Planning.Actions;

// Reads a plan item's params. A missing or wrong field means the plan file was not written by
// the matching propose command (or was edited by hand), so it fails the item, not the commit.
public static class ActionParams
{
    public static string Text(JsonElement parameters, string name) =>
        OptionalText(parameters, name) ?? throw new PlanActionException($"The plan item has no '{name}'.");

    public static string? OptionalText(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static double Number(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number) && double.IsFinite(number)
            ? number
            : throw new PlanActionException($"The plan item has no number '{name}'.");

    // Null when the field is missing or null; a field of another type is an error.
    public static double? OptionalNumber(JsonElement parameters, string name) =>
        !parameters.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null ? null : Number(parameters, name);

    public static bool? OptionalBool(JsonElement parameters, string name) =>
        !parameters.TryGetProperty(name, out var value) ? null : value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => throw new PlanActionException($"The plan item's '{name}' must be true or false."),
        };

    public static ElementId Id(JsonElement parameters, string name)
    {
        var text = Text(parameters, name);
        return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id)
            ? new ElementId(id)
            : throw new PlanActionException($"'{text}' is not an element ID.");
    }

    public static double Feet(double millimeters) => UnitUtils.ConvertToInternalUnits(millimeters, UnitTypeId.Millimeters);
}
