using System.Text.Json;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands;
using Bimtech.StructuralCore.Columns;

namespace Bimtech.RevitMcp.Planning.Actions;

// Writes the items of propose_columns: one column group between two levels.
//   { "baseLevelId": "..", "topLevelId": "..", "typeId": "..." | null, "templateFamilyId": "..." | null,
//     "sizeAMm": 300, "sizeBMm": 500, "mark": "C1" | null,
//     "columns": [{ "x": .., "y": .., "angleDeg": 0, "lengthMm": 500, "widthMm": 300 }] }   (mm)
// Without typeId the b×h type is reused or created in templateFamilyId. Columns already in the model at that spot
// (any family, overlapping heights) are skipped. The mark goes to the Mark parameter.
public sealed class CreateColumnsAction : IPlanAction
{
    public string Action => "create_columns";

    private const int MaxIdsReturned = 500;

    public object Apply(Document document, JsonElement parameters)
    {
        var baseLevel = document.GetElement(ActionParams.Id(parameters, "baseLevelId")) as Level
            ?? throw new PlanActionException("The base level no longer exists.");
        var topLevel = document.GetElement(ActionParams.Id(parameters, "topLevelId")) as Level
            ?? throw new PlanActionException("The top level no longer exists.");
        if (!parameters.TryGetProperty("columns", out var columns) || columns.ValueKind != JsonValueKind.Array)
            throw new PlanActionException("The plan item has no 'columns'.");
        var mark = ActionParams.OptionalText(parameters, "mark");

        try
        {
            var (symbol, typeCreated) = ColumnType(document, parameters);
            var existing = new ColumnPlacer.DuplicateChecker(document, baseLevel, topLevel);
            var ids = new List<ElementId>();
            var skipped = 0;
            foreach (var column in columns.EnumerateArray())
            {
                var raw = new RawColumn
                {
                    Center = new XYZ(ActionParams.Feet(ActionParams.Number(column, "x")), ActionParams.Feet(ActionParams.Number(column, "y")), 0),
                    LengthFeet = ActionParams.Feet(ActionParams.Number(column, "lengthMm")),
                    WidthFeet = ActionParams.Feet(ActionParams.Number(column, "widthMm")),
                    AngleRadians = ActionParams.Number(column, "angleDeg") * Math.PI / 180,
                    Segments = [],
                };
                if (existing.Find(raw.Center) is not null)
                {
                    skipped++;
                    continue;
                }
                var id = ColumnPlacer.PlaceColumnInTransaction(document, raw, symbol, baseLevel, topLevel);
                if (mark is not null) document.GetElement(id)?.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(mark);
                ids.Add(id);
            }

            return new
            {
                created = ids.Count,
                skippedExisting = skipped,
                type = new { id = RevitJson.Id(symbol.Id), name = symbol.Name, family = symbol.FamilyName, created = typeCreated },
                mark,
                columnIds = ids.Take(MaxIdsReturned).Select(RevitJson.Id).ToList(),
            };
        }
        catch (InvalidOperationException exception)
        {
            throw new PlanActionException(exception.Message);
        }
    }

    private static (FamilySymbol Symbol, bool Created) ColumnType(Document document, JsonElement parameters)
    {
        if (ActionParams.OptionalText(parameters, "typeId") is not null)
            return document.GetElement(ActionParams.Id(parameters, "typeId")) is FamilySymbol symbol
                ? (symbol, false)
                : throw new PlanActionException("The column type no longer exists.");
        var family = document.GetElement(ActionParams.Id(parameters, "templateFamilyId")) as Family
            ?? throw new PlanActionException("The column family no longer exists.");
        return ColumnPlacer.ResolveOrCreateColumnType(document,
            ActionParams.Number(parameters, "sizeAMm"), ActionParams.Number(parameters, "sizeBMm"), family);
    }
}
