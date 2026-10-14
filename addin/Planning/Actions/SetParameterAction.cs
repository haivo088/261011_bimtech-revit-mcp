using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace Bimtech.RevitMcp.Planning.Actions;

// Writes the items of propose_set_parameters:
//   { "elementId": "12345", "parameter": "Comments", "value": "A", "currentValue": "...", "storageType": "String" }
// The value is parsed the same way the propose command checked it (model units for numbers).
public sealed class SetParameterAction : IPlanAction
{
    public string Action => "set_parameter";

    public object Apply(Document document, JsonElement parameters)
    {
        var elementId = Text(parameters, "elementId");
        var parameterName = Text(parameters, "parameter");
        var value = Text(parameters, "value");

        if (!long.TryParse(elementId, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id))
            throw new PlanActionException($"'{elementId}' is not an element ID.");
        var element = document.GetElement(new ElementId(id))
            ?? throw new PlanActionException("The element no longer exists.");
        var parameter = element.LookupParameter(parameterName)
            ?? throw new PlanActionException($"The element has no parameter named '{parameterName}'.");
        if (parameter.IsReadOnly)
            throw new PlanActionException($"'{parameterName}' is read-only.");

        var previous = parameter.AsValueString() ?? parameter.AsString();
        var written = parameter.StorageType switch
        {
            StorageType.String => parameter.Set(value),
            StorageType.Integer => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? parameter.Set(number)
                : throw new PlanActionException($"'{parameterName}' needs a whole number."),
            StorageType.Double => UnitFormatUtils.TryParse(document.GetUnits(), parameter.Definition.GetDataType(), value, out double parsed)
                ? parameter.Set(parsed)
                : throw new PlanActionException($"'{value}' is not a valid value for '{parameterName}' in this model's units."),
            _ => throw new PlanActionException($"Parameters that store {parameter.StorageType} are not supported."),
        };
        if (!written)
            throw new PlanActionException($"Revit did not accept '{value}' for '{parameterName}'.");

        return new
        {
            elementId,
            parameter = parameterName,
            previousValue = previous,
            newValue = parameter.AsValueString() ?? parameter.AsString(),
        };
    }

    private static string Text(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new PlanActionException($"The plan item has no '{name}'.");
}
