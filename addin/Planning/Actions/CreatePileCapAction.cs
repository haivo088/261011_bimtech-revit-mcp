using System.Text.Json;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands;
using Bimtech.StructuralCore.Piles;

namespace Bimtech.RevitMcp.Planning.Actions;

// Writes the items of propose_piles: one pile cap with its piles and lean concrete, in one transaction.
//   { "capLevelId": "..", "outline": [[x, y], ...], "capTypeId": "..." | null, "capThicknessMm": 1200,
//     "capTemplateTypeId": "..." | null, "lotTypeId": "..." | null, "lotOffsetMm": 50, "embedmentMm": 100,
//     "pileLengthMm": 32000 | null, "join": true,
//     "piles": [{ "x": .., "y": .., "diameterMm": 600, "typeId": "..." | null, "templateTypeId": "..." | null }] }   (mm)
// The cap type is the given one, else a type of that thickness (reused or created as BTC-DaiMong_<h>). Pile tops sit at
// the cap level minus that cap's thickness. Piles and the cap are joined with each other only, never with the rest of
// the model. A cap of about that size already modelled at that spot, or a pile there, is skipped, not duplicated.
public sealed class CreatePileCapAction : IPlanAction
{
    public string Action => "create_pile_cap";

    // Pile length parameter names seen in pile families; the first one present and writable is set.
    private static readonly string[] LengthParams = ["Length", "Pile Length", "Chiều dài", "Chieu dai", "L"];

    public object Apply(Document document, JsonElement parameters)
    {
        var level = document.GetElement(ActionParams.Id(parameters, "capLevelId")) as Level
            ?? throw new PlanActionException("The cap level no longer exists.");
        var outline = Outline(parameters, level.Elevation);
        var lotType = ActionParams.OptionalText(parameters, "lotTypeId") is null
            ? null
            : document.GetElement(ActionParams.Id(parameters, "lotTypeId")) as FloorType
              ?? throw new PlanActionException("The lean-concrete type no longer exists.");

        var center = Center(outline);
        var corners = outline.Select(curve => curve.GetEndPoint(0)).ToList();
        var (sizeX, sizeY) = (corners.Max(p => p.X) - corners.Min(p => p.X), corners.Max(p => p.Y) - corners.Min(p => p.Y));
        if (PileFoundationPlacer.FindDuplicateCap(center, sizeX, sizeY, PileFoundationPlacer.ExistingCapBoxes(document, lotType?.Id)) is { } existing)
            return new { skipped = $"A cap already exists here ({RevitJson.Id(existing)}); nothing was added." };

        try
        {
            var (capType, capTypeCreated) = CapType(document, parameters);
            var cap = Floor.Create(document, [outline], capType.Id, level.Id, true, null, 0.0);
            var thickness = PileFoundationPlacer.CapThicknessFeet(capType)
                ?? throw new PlanActionException($"Cap type \"{capType.Name}\" has no layer structure, so its thickness is unknown.");

            var warnings = new List<string>();
            var (pileIds, skippedPiles, createdPileTypes) = PlacePiles(document, parameters, level, level.Elevation - thickness, warnings);

            Floor? lot = null;
            if (lotType is not null)
            {
                lot = PileLotFloorPlacer.PlaceUnderCapInTransaction(document, cap, outline, lotType,
                    ActionParams.OptionalNumber(parameters, "lotOffsetMm") ?? PileDefaults.LotOffsetMm, level);
                if (lot is null) warnings.Add("Lean concrete already exists here; not added again.");
            }

            JoinSummary? joins = null;
            if ((ActionParams.OptionalBool(parameters, "join") ?? true) && pileIds.Count > 0)
                joins = PileCapJoiner.JoinInTransaction(document, null, pileIds,
                    lot is null ? [cap.Id] : [cap.Id, lot.Id]);

            return new
            {
                capId = RevitJson.Id(cap.Id),
                capType = new { id = RevitJson.Id(capType.Id), name = capType.Name, created = capTypeCreated },
                piles = new { created = pileIds.Count, skippedExisting = skippedPiles, typesCreated = createdPileTypes },
                pileIds = pileIds.Select(RevitJson.Id).ToList(),
                lotId = lot is null ? null : RevitJson.Id(lot.Id),
                joins = joins is null ? null : new { joins.Joined, joins.AlreadyJoined, joins.Failed },
                warnings,
            };
        }
        catch (InvalidOperationException exception)
        {
            throw new PlanActionException(exception.Message);
        }
    }

    private static (FloorType Type, bool Created) CapType(Document document, JsonElement parameters)
    {
        if (ActionParams.OptionalText(parameters, "capTypeId") is not null)
            return document.GetElement(ActionParams.Id(parameters, "capTypeId")) is FloorType type
                ? (type, false)
                : throw new PlanActionException("The cap type no longer exists.");
        var thicknessMm = ActionParams.OptionalNumber(parameters, "capThicknessMm")
            ?? throw new PlanActionException("The cap thickness is unknown.");
        var template = ActionParams.OptionalText(parameters, "capTemplateTypeId") is null
            ? null
            : document.GetElement(ActionParams.Id(parameters, "capTemplateTypeId")) as FloorType;
        if (template is null)
            return PileDefaults.FindByThickness(PileDefaults.CollectFoundationSlabTypes(document), thicknessMm) is { } found
                ? (found, false)
                : throw new PlanActionException($"No Foundation Slab type of {thicknessMm:0} mm and no template type to create one.");
        return PileDefaults.ResolveOrCreateCapType(document, thicknessMm, template);
    }

    private static (List<ElementId> Ids, int Skipped, List<string> TypesCreated) PlacePiles(
        Document document, JsonElement parameters, Level level, double topZ, List<string> warnings)
    {
        var embedment = ActionParams.Feet(ActionParams.OptionalNumber(parameters, "embedmentMm") ?? PileFoundationPlacer.DefaultEmbedmentMm);
        var lengthMm = ActionParams.OptionalNumber(parameters, "pileLengthMm");
        var ids = new List<ElementId>();
        var typesCreated = new List<string>();
        var skipped = 0;
        var boxesByFamily = new Dictionary<string, List<(PileFoundationPlacer.PlanBox Box, ElementId Id)>>();
        if (!parameters.TryGetProperty("piles", out var piles) || piles.ValueKind != JsonValueKind.Array) return (ids, 0, typesCreated);

        foreach (var pile in piles.EnumerateArray())
        {
            var symbol = PileSymbol(document, pile, typesCreated);
            var point = new XYZ(ActionParams.Feet(ActionParams.Number(pile, "x")), ActionParams.Feet(ActionParams.Number(pile, "y")), 0);
            if (!boxesByFamily.TryGetValue(symbol.Family.Name, out var boxes))
                boxesByFamily[symbol.Family.Name] = boxes = PileFoundationPlacer.ExistingPileBoxes(document, symbol.Family.Name);
            if (PileFoundationPlacer.FindDuplicate(point, boxes) is not null)
            {
                skipped++;
                continue;
            }

            var id = PileFoundationPlacer.PlacePileInTransaction(document, point, symbol, level, topZ, embedment);
            ids.Add(id);
            if (lengthMm is { } length && !SetLength(document.GetElement(id), length) && !warnings.Any(w => w.StartsWith("Pile length")))
                warnings.Add($"Pile length not set: family \"{symbol.Family.Name}\" has no writable {string.Join("/", LengthParams)} parameter.");
        }
        return (ids, skipped, typesCreated);
    }

    private static FamilySymbol PileSymbol(Document document, JsonElement pile, List<string> typesCreated)
    {
        if (ActionParams.OptionalText(pile, "typeId") is not null)
            return document.GetElement(ActionParams.Id(pile, "typeId")) as FamilySymbol
                ?? throw new PlanActionException("A pile type no longer exists.");
        var template = document.GetElement(ActionParams.Id(pile, "templateTypeId")) as FamilySymbol
            ?? throw new PlanActionException("The template pile type no longer exists.");
        var (symbol, created) = PileFoundationPlacer.ResolveOrCreatePileType(document, ActionParams.Number(pile, "diameterMm"), template);
        if (created) typesCreated.Add(symbol.Name);
        return symbol;
    }

    private static bool SetLength(Element? pile, double lengthMm)
    {
        foreach (var name in LengthParams)
            if (pile?.LookupParameter(name) is { IsReadOnly: false, StorageType: StorageType.Double } parameter)
                return parameter.Set(ActionParams.Feet(lengthMm));
        return false;
    }

    private static CurveLoop Outline(JsonElement parameters, double z)
    {
        if (!parameters.TryGetProperty("outline", out var points) || points.ValueKind != JsonValueKind.Array || points.GetArrayLength() < 3)
            throw new PlanActionException("The plan item has no cap outline.");
        var vertices = points.EnumerateArray()
            .Select(point => point.ValueKind == JsonValueKind.Array && point.GetArrayLength() == 2
                ? new XYZ(ActionParams.Feet(point[0].GetDouble()), ActionParams.Feet(point[1].GetDouble()), z)
                : throw new PlanActionException("Each outline point must be [x, y] in mm."))
            .ToList();
        var loop = new CurveLoop();
        for (var i = 0; i < vertices.Count; i++)
        {
            var (start, end) = (vertices[i], vertices[(i + 1) % vertices.Count]);
            if (start.DistanceTo(end) >= ActionParams.Feet(1)) loop.Append(Line.CreateBound(start, end));
        }
        return loop;
    }

    private static XYZ Center(CurveLoop loop)
    {
        var points = loop.Select(curve => curve.GetEndPoint(0)).ToList();
        return new XYZ(points.Average(p => p.X), points.Average(p => p.Y), 0);
    }
}
