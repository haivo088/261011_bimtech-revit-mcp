using System.Text.Json;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands;
using Bimtech.StructuralCore.Floors;

namespace Bimtech.RevitMcp.Planning.Actions;

// Writes the items of propose_floors and propose_floors_from_data: one structural floor per item.
//   { "levelId": "..", "offsetMm": 0, "floorTypeId": "123" | null, "thicknessMm": 120 | null,
//     "points": [[x, y], ...], "holes": [[[x, y], ...], ...] | null, "comment": "Vùng DOLMIT" | null }   (mm, model)
// Without floorTypeId the company type BTC_SanKC_<thicknessMm> is used (150 when no thickness), created if missing.
// Holes are openings cut through the floor. A floor that overlaps an existing floor is deleted again by FloorPlacer
// and reported as skipped, not as a failure.
public sealed class CreateFloorAction : IPlanAction
{
    public string Action => "create_floor";

    public object Apply(Document document, JsonElement parameters)
    {
        var level = document.GetElement(ActionParams.Id(parameters, "levelId")) as Level
            ?? throw new PlanActionException("The level no longer exists.");
        var outline = Outline(parameters.TryGetProperty("points", out var points) ? points : default, "points");
        var holes = parameters.TryGetProperty("holes", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select((hole, i) => Outline(hole, $"holes[{i}]")).ToList()
            : [];
        var thickness = parameters.TryGetProperty("thicknessMm", out var t) && t.ValueKind == JsonValueKind.Number
            ? t.GetDouble()
            : FloorDefaults.DefaultFloorThicknessMm;

        FloorType type;
        try
        {
            type = ActionParams.OptionalText(parameters, "floorTypeId") is null
                ? FloorDefaults.CreateCompanyTypeInTransaction(document, thickness)
                : document.GetElement(ActionParams.Id(parameters, "floorTypeId")) as FloorType
                    ?? throw new PlanActionException("The floor type no longer exists.");
        }
        catch (InvalidOperationException exception)
        {
            throw new PlanActionException(exception.Message);
        }

        var summary = FloorPlacer.PlaceInTransaction(document,
            [new FloorPlacement("Sàn", outline, holes, type, level, ActionParams.Number(parameters, "offsetMm"))]);
        if (summary.Failed > 0)
            throw new PlanActionException(string.Join(" | ", summary.Errors));
        if (ActionParams.OptionalText(parameters, "comment") is { } comment && summary.CreatedIds.FirstOrDefault() is { } id)
            document.GetElement(id)?.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(comment);

        return new
        {
            floorId = summary.CreatedIds.Select(RevitJson.Id).FirstOrDefault(),
            skipped = summary.Skipped > 0 ? "Overlaps an existing floor; not created." : null,
            holes = holes.Count,
            type = new { id = RevitJson.Id(type.Id), name = type.Name },
        };
    }

    // Lines between consecutive points, closed back to the first; segments under 1 mm are dropped.
    private static List<Curve> Outline(JsonElement points, string name)
    {
        if (points.ValueKind != JsonValueKind.Array)
            throw new PlanActionException($"The plan item has no '{name}'.");
        var vertices = points.EnumerateArray().Select(point =>
            point.ValueKind == JsonValueKind.Array && point.GetArrayLength() == 2
                && point.EnumerateArray().All(value => value.ValueKind == JsonValueKind.Number)
                ? new XYZ(ActionParams.Feet(point[0].GetDouble()), ActionParams.Feet(point[1].GetDouble()), 0)
                : throw new PlanActionException("Each point must be [x, y] in mm.")).ToList();

        var minLength = ActionParams.Feet(1);
        var curves = new List<Curve>();
        for (var i = 0; i < vertices.Count; i++)
        {
            var start = vertices[i];
            var end = vertices[(i + 1) % vertices.Count];
            if (start.DistanceTo(end) >= minLength) curves.Add(Line.CreateBound(start, end));
        }
        if (curves.Count < 3) throw new PlanActionException($"The outline '{name}' needs at least 3 sides.");
        return curves;
    }
}
