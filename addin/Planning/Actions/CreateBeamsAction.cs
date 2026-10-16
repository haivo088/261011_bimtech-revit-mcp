using System.Text.Json;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands;
using Bimtech.StructuralCore.Beams;

namespace Bimtech.RevitMcp.Planning.Actions;

// Writes the items of propose_beams: one beam group on a level.
//   { "levelId": "..", "offsetMm": 0, "typeId": "..." | null, "templateFamilyId": "..." | null,
//     "sizeBMm": 220, "sizeHMm": 600, "mark": "D1" | null,
//     "beams": [{ "x0": .., "y0": .., "x1": .., "y1": .. }] }   (mm, model coordinates)
// Without typeId the b×h type is reused or created in templateFamilyId. Beams already in the model on the same axis
// are skipped. The mark goes to the Mark parameter.
public sealed class CreateBeamsAction : IPlanAction
{
    public string Action => "create_beams";

    private const int MaxIdsReturned = 500;

    public object Apply(Document document, JsonElement parameters)
    {
        var level = document.GetElement(ActionParams.Id(parameters, "levelId")) as Level
            ?? throw new PlanActionException("The level no longer exists.");
        if (!parameters.TryGetProperty("beams", out var beams) || beams.ValueKind != JsonValueKind.Array)
            throw new PlanActionException("The plan item has no 'beams'.");
        var z = level.Elevation + ActionParams.Feet(ActionParams.Number(parameters, "offsetMm"));
        var mark = ActionParams.OptionalText(parameters, "mark");

        try
        {
            var (symbol, typeCreated) = BeamType(document, parameters);
            var existing = BeamPlacer.CollectExistingBeamLines(document);
            var ids = new List<ElementId>();
            var skipped = 0;
            foreach (var beam in beams.EnumerateArray())
            {
                var start = new XYZ(ActionParams.Feet(ActionParams.Number(beam, "x0")), ActionParams.Feet(ActionParams.Number(beam, "y0")), z);
                var end = new XYZ(ActionParams.Feet(ActionParams.Number(beam, "x1")), ActionParams.Feet(ActionParams.Number(beam, "y1")), z);
                var line = new BeamPlacer.BeamLine(start, end);
                if (existing.Any(other => BeamPlacer.AreDuplicate(other, line)))
                {
                    skipped++;
                    continue;
                }
                ids.Add(BeamPlacer.PlaceBeamInTransaction(document, start, end, symbol, level, mark));
                existing.Add(line);
            }

            return new
            {
                created = ids.Count,
                skippedExisting = skipped,
                type = new { id = RevitJson.Id(symbol.Id), name = symbol.Name, family = symbol.FamilyName, created = typeCreated },
                mark,
                beamIds = ids.Take(MaxIdsReturned).Select(RevitJson.Id).ToList(),
            };
        }
        catch (InvalidOperationException exception)
        {
            throw new PlanActionException(exception.Message);
        }
    }

    private static (FamilySymbol Symbol, bool Created) BeamType(Document document, JsonElement parameters)
    {
        if (ActionParams.OptionalText(parameters, "typeId") is not null)
            return document.GetElement(ActionParams.Id(parameters, "typeId")) is FamilySymbol symbol
                ? (symbol, false)
                : throw new PlanActionException("The beam type no longer exists.");
        var family = document.GetElement(ActionParams.Id(parameters, "templateFamilyId")) as Family
            ?? throw new PlanActionException("The beam family no longer exists.");
        return BeamPlacer.ResolveOrCreateBeamType(document,
            ActionParams.Number(parameters, "sizeBMm"), ActionParams.Number(parameters, "sizeHMm"), family);
    }
}
