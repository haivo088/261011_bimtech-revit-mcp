using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands;

namespace Bimtech.RevitMcp.Planning.Actions;

// Writes the items of propose_change_type: elements of one current type changed to the target type.
//   { "fromTypeId": "123", "fromTypeName": "CW 102-85-100p", "typeId": "456", "typeName": "BTC-VachKC_300",
//     "elementIds": ["789", ...] }
// Elements deleted since the plan are counted as missing, elements already of the target type are skipped; an
// element Revit refuses fails alone. The item fails only when no element could be changed.
public sealed class ChangeTypeAction : IPlanAction
{
    public string Action => "change_type";

    private const int MaxIdsReturned = 500;

    public object Apply(Document document, JsonElement parameters)
    {
        var typeId = ActionParams.Id(parameters, "typeId");
        var type = document.GetElement(typeId) as ElementType
            ?? throw new PlanActionException("The target type no longer exists.");
        if (type is FamilySymbol { IsActive: false } symbol) symbol.Activate();

        int missing = 0, already = 0, failed = 0;
        var changed = new List<ElementId>();
        var errors = new List<string>();
        foreach (var id in ElementIds(parameters))
        {
            var element = document.GetElement(id);
            if (element is null) { missing++; continue; }
            if (element.GetTypeId() == typeId) { already++; continue; }
            try
            {
                element.ChangeTypeId(typeId);
                changed.Add(id);
            }
            catch (Exception exception) when (exception is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException)
            {
                failed++;
                if (errors.Count < 20) errors.Add($"{RevitJson.Id(id)}: {exception.Message}");
            }
        }
        if (changed.Count == 0 && failed > 0)
            throw new PlanActionException("No element could be changed: " + string.Join(" | ", errors));

        return new
        {
            changed = changed.Count,
            alreadyOfType = already,
            missing,
            failed,
            errors,
            type = new { id = RevitJson.Id(type.Id), name = type.Name },
            elementIds = changed.Take(MaxIdsReturned).Select(RevitJson.Id).ToList(),
        };
    }

    private static List<ElementId> ElementIds(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("elementIds", out var ids) || ids.ValueKind != JsonValueKind.Array)
            throw new PlanActionException("The plan item has no 'elementIds'.");
        return ids.EnumerateArray().Select(id =>
            id.ValueKind == JsonValueKind.String
                && long.TryParse(id.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                ? new ElementId(value)
                : throw new PlanActionException("Each element ID must be a digit string.")).ToList();
    }
}
