using System.Text.Json;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands.Plans;
using Bimtech.RevitMcp.Planning;
using Bimtech.RevitMcp.Protocol;

namespace Bimtech.RevitMcp.Commands.Parameters;

// Proposes setting one parameter to one value on many elements. Read-only:
// it checks every element and saves a plan, but never opens a Transaction.
// This is the simplest propose command, used to try the plan flow end to end.
public sealed class ProposeSetParametersCommand(PlanService plans) : IRevitCommand
{
    public string Method => "propose_set_parameters";

    private const int MaxElements = 1000;

    public object? Execute(CommandContext context)
    {
        var uiDocument = context.RequireUIDocument();
        var document = uiDocument.Document;
        var parameterName = context.Params.RequireString("parameter", 256);
        var value = context.Params.RequireString("value");

        // Without elementIds, use what the person has selected in Revit.
        var fromSelection = !context.Params.Has("elementIds");
        var ids = (fromSelection
                ? uiDocument.Selection.GetElementIds().ToList()
                : context.Params.GetElementIds("elementIds", MaxElements))
            .Distinct().ToList();
        if (ids.Count == 0)
            throw RpcException.InvalidParams("elementIds", "select elements in Revit or pass elementIds.");
        if (ids.Count > MaxElements)
            throw RpcException.InvalidParams("elementIds", $"at most {MaxElements} elements per plan; {ids.Count} are selected.");

        var items = new List<PlanItem>();
        var unchanged = new List<string>();
        foreach (var id in ids)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            var item = Check(document, id, parameterName, value, out var alreadySet);
            if (alreadySet) unchanged.Add(RevitJson.Id(id));
            else items.Add(item);
        }
        if (items.Count == 0)
            return new
            {
                plan = (object?)null,
                unchangedElementIds = unchanged,
                note = "Every element already has this value. No plan was created.",
            };

        var plan = plans.Create(
            PlanStorageLocation.For(document),
            kind: "set_parameters",
            title: $"Set '{parameterName}' to '{value}' on {items.Count} element(s)",
            scope: JsonSerializer.SerializeToElement(new
            {
                parameter = parameterName,
                value,
                source = fromSelection ? "selection" : "elementIds",
                elementCount = ids.Count,
            }),
            items: items);

        return new
        {
            plan = PlanResults.Status(plan),
            unchangedElementIds = unchanged,
            note = "Nothing was changed in the model. The plan waits for review; "
                + "use get_plan to show the user each item and its reasons.",
        };
    }

    // Builds one item. Problems become Blocked items with a reason instead of errors,
    // so one bad element does not hide the rest of the plan.
    private static PlanItem Check(Document document, ElementId id, string parameterName, string value, out bool alreadySet)
    {
        alreadySet = false;
        var element = document.GetElement(id);
        var parameter = element?.LookupParameter(parameterName);
        var currentValue = parameter is null ? null : parameter.AsValueString() ?? parameter.AsString();

        string? problem = null;
        if (element is null) problem = "No element has this ID.";
        else if (parameter is null) problem = $"The element has no instance parameter named '{parameterName}'.";
        else if (parameter.IsReadOnly) problem = $"'{parameterName}' is read-only.";
        else problem = CheckValue(document, parameter, value);

        if (problem is null && currentValue == value) alreadySet = true;

        return new PlanItem
        {
            ItemId = "E" + RevitJson.Id(id),
            Action = "set_parameter",
            Params = JsonSerializer.SerializeToElement(new
            {
                elementId = RevitJson.Id(id),
                parameter = parameterName,
                value,
                currentValue,
                storageType = parameter?.StorageType.ToString(),
            }),
            Tier = problem is null ? PlanTier.Review : PlanTier.Blocked,
            Reasons = problem is null ? [] : [problem],
            Source = JsonSerializer.SerializeToElement(new
            {
                elementId = RevitJson.Id(id),
                name = element?.Name,
                category = element?.Category?.Name,
            }),
        };
    }

    // Returns why the text cannot be stored in this parameter, or null if it can.
    private static string? CheckValue(Document document, Parameter parameter, string value)
    {
        switch (parameter.StorageType)
        {
            case StorageType.String:
                return null;
            case StorageType.Integer:
                return int.TryParse(value, out _) ? null : $"'{parameter.Definition.Name}' needs a whole number.";
            case StorageType.Double:
                try
                {
                    // Parses with the model's units, e.g. "300" or "300 mm" for a length.
                    return UnitFormatUtils.TryParse(document.GetUnits(), parameter.Definition.GetDataType(), value, out _)
                        ? null
                        : $"'{value}' is not a valid value for '{parameter.Definition.Name}' in this model's units.";
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    return $"Cannot read '{value}' as a value for '{parameter.Definition.Name}'.";
                }
            default:
                return $"Parameters that store {parameter.StorageType} are not supported yet.";
        }
    }
}
