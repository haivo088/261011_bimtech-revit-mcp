using System.Text.Json;
using Autodesk.Revit.DB;
using Bimtech.StructuralCore.Levels;

namespace Bimtech.RevitMcp.Planning.Actions;

// Writes the delete items of propose_levels: removes a level nobody uses, with its own views.
//   { "levelId": "311", "levelName": "Level 2" }
// Checked again just before deleting; if something now depends on the level, nothing is deleted.
public sealed class DeleteLevelAction : IPlanAction
{
    public string Action => "delete_level";

    public object Apply(Document document, JsonElement parameters)
    {
        var level = document.GetElement(ActionParams.Id(parameters, "levelId")) as Level
            ?? throw new PlanActionException("The level no longer exists.");
        try
        {
            var deleted = LevelSetup.DeleteUnusedInTransaction(document, level);
            return new { deletedLevel = ActionParams.OptionalText(parameters, "levelName"), deletedElements = deleted };
        }
        catch (InvalidOperationException exception)
        {
            throw new PlanActionException(exception.Message);
        }
    }
}
