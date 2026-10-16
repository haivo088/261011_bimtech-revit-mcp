using System.Text.Json;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands;
using Bimtech.StructuralCore.Floors;

namespace Bimtech.RevitMcp.Planning.Actions;

// Writes the items of propose_floors: one structural floor panel per item.
//   { "levelId": "..", "offsetMm": 0, "floorTypeId": "123" | null, "points": [[x, y], ...] }   (mm, model coordinates)
// Without floorTypeId the company type BTC_SanKC_150 is used, created if missing. A panel that overlaps an
// existing floor is deleted again by FloorPlacer and reported as skipped, not as a failure.
public sealed class CreateFloorAction : IPlanAction
{
    public string Action => "create_floor";

    public object Apply(Document document, JsonElement parameters)
    {
        var level = document.GetElement(ActionParams.Id(parameters, "levelId")) as Level
            ?? throw new PlanActionException("The level no longer exists.");
        var outline = Outline(parameters);

        FloorType type;
        try
        {
            type = ActionParams.OptionalText(parameters, "floorTypeId") is null
                ? FloorDefaults.CreateDefaultFloorTypeInTransaction(document)
                : document.GetElement(ActionParams.Id(parameters, "floorTypeId")) as FloorType
                    ?? throw new PlanActionException("The floor type no longer exists.");
        }
        catch (InvalidOperationException exception)
        {
            throw new PlanActionException(exception.Message);
        }

        var summary = FloorPlacer.PlaceInTransaction(document,
            [new FloorPlacement("Sàn", outline, [], type, level, ActionParams.Number(parameters, "offsetMm"))]);
        if (summary.Failed > 0)
            throw new PlanActionException(string.Join(" | ", summary.Errors));

        return new
        {
            floorId = summary.CreatedIds.Select(RevitJson.Id).FirstOrDefault(),
            skipped = summary.Skipped > 0 ? "Overlaps an existing floor; not created." : null,
            type = new { id = RevitJson.Id(type.Id), name = type.Name },
        };
    }

    // Lines between consecutive points, closed back to the first; segments under 1 mm are dropped.
    private static List<Curve> Outline(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("points", out var points) || points.ValueKind != JsonValueKind.Array)
            throw new PlanActionException("The plan item has no 'points'.");
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
        if (curves.Count < 3) throw new PlanActionException("The outline needs at least 3 sides.");
        return curves;
    }
}
