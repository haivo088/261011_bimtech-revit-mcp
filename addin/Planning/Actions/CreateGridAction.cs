using System.Text.Json;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands;
using Bimtech.StructuralCore.Levels;

namespace Bimtech.RevitMcp.Planning.Actions;

// Writes the grid items of propose_grids: one grid line per item, in model coordinates (mm), pinned unless pin is false.
//   { "name": "B", "startMm": { "x": .., "y": .. }, "endMm": { "x": .., "y": .. }, "pin": true, ... }
public sealed class CreateGridAction : IPlanAction
{
    public string Action => "create_grid";

    public object Apply(Document document, JsonElement parameters)
    {
        var name = ActionParams.Text(parameters, "name");
        var start = Point(parameters, "startMm");
        var end = Point(parameters, "endMm");
        if (start.DistanceTo(end) < ActionParams.Feet(1))
            throw new PlanActionException("The grid line has no length.");

        try
        {
            var grid = GridBuilder.CreateNamedGrid(document, Line.CreateBound(start, end), name);
            // Grids are what every later element is placed against; pinning stops an accidental drag.
            if (ActionParams.OptionalBool(parameters, "pin") ?? true) grid.Pinned = true;
            return new { gridId = RevitJson.Id(grid.Id), name, pinned = grid.Pinned };
        }
        catch (InvalidOperationException exception)
        {
            throw new PlanActionException(exception.Message);
        }
    }

    private static XYZ Point(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var point) && point.ValueKind == JsonValueKind.Object
            ? new XYZ(ActionParams.Feet(ActionParams.Number(point, "x")), ActionParams.Feet(ActionParams.Number(point, "y")), 0)
            : throw new PlanActionException($"The plan item has no '{name}'.");
}
