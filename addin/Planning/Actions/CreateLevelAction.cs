using System.Text.Json;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands;
using Bimtech.StructuralCore.Levels;

namespace Bimtech.RevitMcp.Planning.Actions;

// Writes the items of propose_levels: one storey per item.
//   { "levelName": "Tầng 2", "elevationMm": 3600, "cadFile": "D:\\...\\S-201.dwg" | null, "mode": "Import" | "Link", ... }
// Creates the level if missing (or renames the level of another name at that elevation, renameLevelId), its
// structural plan "KC - <name>" if missing, and imports or links the CAD into that plan at the origin
// (LevelSetup, the same steps as the WPF tool, without grids).
public sealed class CreateLevelAction : IPlanAction
{
    public string Action => "create_level";

    public object Apply(Document document, JsonElement parameters)
    {
        var mode = ActionParams.OptionalText(parameters, "mode") is { } text
            ? Enum.TryParse<CadPlacementMode>(text, ignoreCase: true, out var parsed)
                ? parsed
                : throw new PlanActionException($"Unknown CAD mode '{text}'.")
            : CadPlacementMode.Import;
        var row = new LevelSetupRow(
            ActionParams.OptionalText(parameters, "cadFile") ?? "",
            ActionParams.Text(parameters, "levelName"),
            ActionParams.Number(parameters, "elevationMm"),
            mode,
            CreateGrids: false);
        var planType = LevelSetup.FindStructuralPlanType(document)
            ?? throw new PlanActionException("The model has no Structural Plan view type.");

        LevelSetup.RowResult result;
        try
        {
            var renameLevelId = ActionParams.OptionalText(parameters, "renameLevelId") is null
                ? null
                : ActionParams.Id(parameters, "renameLevelId");
            result = LevelSetup.ApplyRowInTransaction(document, planType, row, existingAlignment: null, renameLevelId);
        }
        catch (InvalidOperationException exception)
        {
            throw new PlanActionException(exception.Message);
        }

        return new
        {
            levelId = RevitJson.Id(result.LevelId),
            viewId = RevitJson.Id(result.PlanId),
            cadId = result.CadId is null ? null : RevitJson.Id(result.CadId),
        };
    }
}
