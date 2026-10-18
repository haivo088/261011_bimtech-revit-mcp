using System.Text.Json;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands;
using Bimtech.StructuralCore.Walls;

namespace Bimtech.RevitMcp.Planning.Actions;

// Writes the items of propose_walls: one wall group per item, placed between two levels.
//   { "thicknessMm": 200, "tagName": "V1", "typeId": "123" | null, "templateTypeId": "456" | null,
//     "companyTypeOnly": false, "baseLevelId": "..", "topLevelId": "..", "baseOffsetMm": 0, "topOffsetMm": 0,
//     "tagParameterName": null, "lines": [[x0, y0, x1, y1], ...] }   (mm, model coordinates)
// Without typeId, a type matching by Recognition.WallTypes is reused (also one an earlier item just created; with
// companyTypeOnly only a BTC-VachKC_ type), else "BTC-VachKC_<b>" is duplicated from templateTypeId. Parts of a centerline already covered by a wall are skipped (WallPlacer).
public sealed class CreateWallsAction : IPlanAction
{
    public string Action => "create_walls";

    private const int MaxIdsReturned = 500;

    public object Apply(Document document, JsonElement parameters)
    {
        var thicknessMm = ActionParams.Number(parameters, "thicknessMm");
        var baseLevel = document.GetElement(ActionParams.Id(parameters, "baseLevelId")) as Level
            ?? throw new PlanActionException("The base level no longer exists.");
        var topLevel = document.GetElement(ActionParams.Id(parameters, "topLevelId")) as Level
            ?? throw new PlanActionException("The top level no longer exists.");

        var (type, typeCreated) = ResolveType(document, parameters, thicknessMm);
        var walls = Lines(parameters).Select(line => new RawWall
        {
            Start = new XYZ(ActionParams.Feet(line[0]), ActionParams.Feet(line[1]), 0),
            End = new XYZ(ActionParams.Feet(line[2]), ActionParams.Feet(line[3]), 0),
            WidthFeet = ActionParams.Feet(thicknessMm),
            Segments = [],
        }).ToList();
        var group = new WallSizeGroup { TagName = ActionParams.OptionalText(parameters, "tagName"), SizeBMm = thicknessMm, Walls = walls };

        var summary = WallPlacer.PlaceInTransaction(document, [new WallAssignment(group, type)], baseLevel, topLevel,
            ActionParams.Feet(ActionParams.Number(parameters, "baseOffsetMm")),
            ActionParams.Feet(ActionParams.Number(parameters, "topOffsetMm")),
            ActionParams.OptionalText(parameters, "tagParameterName"));
        if (summary.Created == 0 && summary.Failed > 0)
            throw new PlanActionException("No wall could be created: " + string.Join(" | ", summary.Errors));

        return new
        {
            created = summary.Created,
            skippedExisting = summary.Skipped,
            failed = summary.Failed,
            errors = summary.Errors,
            type = new { id = RevitJson.Id(type.Id), name = type.Name, created = typeCreated },
            wallIds = summary.CreatedIds.Take(MaxIdsReturned).Select(RevitJson.Id).ToList(),
        };
    }

    private static (WallType Type, bool Created) ResolveType(Document document, JsonElement parameters, double thicknessMm)
    {
        if (ActionParams.OptionalText(parameters, "typeId") is not null)
            return document.GetElement(ActionParams.Id(parameters, "typeId")) is WallType { Kind: WallKind.Basic } type
                ? (type, false)
                : throw new PlanActionException("The wall type no longer exists.");

        var template = document.GetElement(ActionParams.Id(parameters, "templateTypeId")) as WallType
            ?? throw new PlanActionException("The template wall type no longer exists.");
        try
        {
            return WallPlacer.ResolveOrCreateType(document, thicknessMm, template,
                companyOnly: ActionParams.OptionalBool(parameters, "companyTypeOnly") == true);
        }
        catch (InvalidOperationException exception)
        {
            throw new PlanActionException(exception.Message);
        }
    }

    private static List<double[]> Lines(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array)
            throw new PlanActionException("The plan item has no 'lines'.");
        return lines.EnumerateArray().Select(line =>
            line.ValueKind == JsonValueKind.Array && line.GetArrayLength() == 4
                && line.EnumerateArray().All(value => value.ValueKind == JsonValueKind.Number)
                ? line.EnumerateArray().Select(value => value.GetDouble()).ToArray()
                : throw new PlanActionException("Each line must be [x0, y0, x1, y1] in mm.")).ToList();
    }
}
