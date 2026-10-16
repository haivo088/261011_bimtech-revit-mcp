using System.Text.Json;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands;
using Bimtech.StructuralCore.Levels;

namespace Bimtech.RevitMcp.Planning.Actions;

// Writes the alignment items of propose_grids: moves one CAD import or link so its grid lands on the model's grid.
// Pinned CAD is unpinned for the move and pinned again; with pin (default true) it is pinned afterwards in any case,
// since scans read positions from it.
//   { "cadId": "12345", "dxMm": -1520.5, "dyMm": 300, "pin": true, ... }
public sealed class MoveCadAction : IPlanAction
{
    public string Action => "move_cad";

    public object Apply(Document document, JsonElement parameters)
    {
        var cadId = ActionParams.Id(parameters, "cadId");
        var cad = document.GetElement(cadId) as ImportInstance
            ?? throw new PlanActionException("The CAD import or link no longer exists.");
        var translation = new XYZ(
            ActionParams.Feet(ActionParams.Number(parameters, "dxMm")),
            ActionParams.Feet(ActionParams.Number(parameters, "dyMm")),
            0);

        GridBuilder.MoveCad(document, cad, translation);
        if (ActionParams.OptionalBool(parameters, "pin") ?? true) cad.Pinned = true;
        return new { cadId = RevitJson.Id(cadId), pinned = cad.Pinned };
    }
}
