using System.Text.Json;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands;
using Bimtech.RevitMcp.Commands.Common;
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
            CreateGrids: false)
        {
            Unit = (ActionParams.OptionalText(parameters, "cadUnit") ?? "mm") switch
            {
                "mm" => ImportUnit.Millimeter,
                "cm" => ImportUnit.Centimeter,
                "m" => ImportUnit.Meter,
                "auto" => ImportUnit.Default,
                var other => throw new PlanActionException($"Unknown CAD unit '{other}'."),
            },
            ViewTemplateId = ActionParams.OptionalText(parameters, "viewTemplateId") is null ? null : ActionParams.Id(parameters, "viewTemplateId"),
            Scale = ActionParams.OptionalNumber(parameters, "scale") is { } scale ? (int)scale : null,
        };
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

        var (cadSizeMm, cadWarning) = result.CadId is null ? (null, null) : CheckCadSize(document, result.CadId, result.PlanId);
        return new
        {
            levelId = RevitJson.Id(result.LevelId),
            viewId = RevitJson.Id(result.PlanId),
            cadId = result.CadId is null ? null : RevitJson.Id(result.CadId),
            cadSizeMm,
            cadWarning,
        };
    }

    // A structural plan spans metres to hundreds of metres. Far smaller or larger means the DWG unit was read wrong
    // (drawn in metres but imported as mm, or the reverse), which the person should know before building on it.
    private const double MinPlausibleSizeMm = 2_000;
    private const double MaxPlausibleSizeMm = 2_000_000;

    private static (double? SizeMm, string? Warning) CheckCadSize(Document document, ElementId cadId, ElementId planId)
    {
        document.Regenerate();
        var box = document.GetElement(cadId)?.get_BoundingBox(document.GetElement(planId) as View);
        if (box is null) return (null, "Không đọc được kích thước CAD vừa nhập.");
        var size = Math.Round(Math.Max(UnitConvert.Length(box.Max.X - box.Min.X), UnitConvert.Length(box.Max.Y - box.Min.Y)));
        return size switch
        {
            < MinPlausibleSizeMm => (size, $"CAD chỉ rộng {size:0} mm: có thể bản vẽ dùng mét nhưng được đọc là mm. "
                + "Huỷ (Ctrl+Z) và propose lại với cadUnit \"m\" hoặc \"auto\"."),
            > MaxPlausibleSizeMm => (size, $"CAD rộng {size / 1000:0} m: có thể đơn vị bị đọc sai, hoặc có đối tượng nằm rất xa gốc toạ độ."),
            _ => (size, null),
        };
    }
}
