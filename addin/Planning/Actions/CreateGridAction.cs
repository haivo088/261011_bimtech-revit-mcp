using System.Text.Json;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands;
using Bimtech.StructuralCore.Levels;

namespace Bimtech.RevitMcp.Planning.Actions;

// Writes the grid items of propose_grids and propose_grids_from_data: one grid line per item, in model coordinates
// (mm), pinned unless pin is false. Optional comments (from data: source file, hash, CAD handle) go to Comments.
//   { "name": "B", "startMm": { "x": .., "y": .. }, "endMm": { "x": .., "y": .. }, "pin": true, "comments": "..." }
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
            var commented = WriteComments(grid, ActionParams.OptionalText(parameters, "comments"));
            return new { gridId = RevitJson.Id(grid.Id), name, pinned = grid.Pinned, commented };
        }
        catch (InvalidOperationException exception)
        {
            throw new PlanActionException(exception.Message);
        }
    }

    // Comments, when the grid has a writable one; null when no comments were asked for.
    private static bool? WriteComments(Grid grid, string? comments)
    {
        if (string.IsNullOrEmpty(comments)) return null;
        var parameter = grid.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
        if (parameter is null || parameter.IsReadOnly) return false;
        return parameter.Set(comments);
    }

    private static XYZ Point(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var point) && point.ValueKind == JsonValueKind.Object
            ? new XYZ(ActionParams.Feet(ActionParams.Number(point, "x")), ActionParams.Feet(ActionParams.Number(point, "y")), 0)
            : throw new PlanActionException($"The plan item has no '{name}'.");
}
