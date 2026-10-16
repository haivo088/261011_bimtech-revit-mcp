using System.Globalization;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands.Common;
using Bimtech.RevitMcp.Protocol;
using Bimtech.RevitMcp.Recognition;
using Bimtech.StructuralCore.Families;
using Bimtech.StructuralCore.Piles;

namespace Bimtech.RevitMcp.Commands.Structural;

// A pile scan kept in SessionStore; propose_piles reads it back by scanId.
internal sealed record PileScan(ElementId CadId, ElementId ViewId, IReadOnlyList<PileSizeGroup> Groups, IReadOnlyList<BoundaryRow> Caps)
{
    public const string Kind = "scan";

    // Caps are grouped by their mark ("Đ1") when the drawing has one, else by size ("2000x2000", rounded to 5 mm).
    public static string CapKey(BoundaryRow cap) => cap.TagName ?? SizeKey(cap);

    public static string SizeKey(BoundaryRow cap) => $"{Mm(cap.Boundary.LengthFeet)}x{Mm(cap.Boundary.WidthFeet)}";

    private static string Mm(double feet) =>
        PileCaps.RoundTo(UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters), 5).ToString("0", CultureInfo.InvariantCulture);
}

// Reads piles (circles) and pile caps (closed outlines) from a CAD in two passes:
//   1. without layers: CAD layers with their circle, outline and loose line counts;
//   2. with pileLayers and/or capLayers: piles grouped by diameter, caps (outlines drawn twice merged, and caps drawn
//      as loose lines joined into loops), each pile assigned to the cap containing it, each cap's mark read from a
//      Text Note in or beside it ("Đ1 (2000x2000x1200)" gives the thickness), and a scanId for propose_piles.
public sealed class ScanCadPilesCommand : IRevitCommand
{
    private const int MaxLayersReturned = 200;
    private const int MaxCapsReturned = 100;

    public string Method => "scan_cad_piles";
    public CommandAccess Access => CommandAccess.Read;

    public object? Execute(CommandContext context)
    {
        var p = context.Params;
        var document = context.RequireDocument();
        var cad = StructuralJson.RequireCad(document, p.RequireElementId("cadId"), "cadId");
        var view = StructuralJson.RequirePlanView(context, document, cad);
        var pileLayers = p.GetStringList("pileLayers", maxCount: 50, maxLength: 255);
        var capLayers = p.GetStringList("capLayers", maxCount: 50, maxLength: 255);
        var region = StructuralJson.ReadRegion(p.GetObject("region"));

        var scan = CadPileScanner.Scan(document, cad, view);
        context.Cancellation.ThrowIfCancellationRequested();

        var missingPiles = pileLayers.Where(layer => !scan.CirclesByLayer.ContainsKey(layer)).ToList();
        if (missingPiles.Count > 0)
            throw RpcException.InvalidParams("pileLayers",
                $"no circles found on layer(s): {string.Join(", ", missingPiles)}. Call without layers to list the layers.");
        var missingCaps = capLayers
            .Where(layer => !scan.BoundariesByLayer.ContainsKey(layer) && !scan.LinesByLayer.ContainsKey(layer)).ToList();
        if (missingCaps.Count > 0)
            throw RpcException.InvalidParams("capLayers",
                $"no closed outlines or lines found on layer(s): {string.Join(", ", missingCaps)}. Call without layers to list the layers.");

        var summaries = scan.GetLayerSummaries();
        var layerInfo = summaries
            .Take(MaxLayersReturned)
            .Select(layer => new
            {
                name = layer.Name,
                circleCount = layer.CircleCount,
                outlineCount = layer.BoundaryCount,
                lineCount = layer.LineCount,
                selectedAs = pileLayers.Contains(layer.Name) ? "pile" : capLayers.Contains(layer.Name) ? "cap" : null,
            })
            .ToList();
        var source = new { cad = StructuralJson.CadRef(document, cad), view = StructuralJson.Ref(view) };

        if (pileLayers.Count == 0 && capLayers.Count == 0)
        {
            return new
            {
                source,
                scanId = (string?)null,
                layerCount = summaries.Count,
                layers = layerInfo,
                next = summaries.Count == 0
                    ? "No circles, closed outlines or lines found. Check that the CAD layers are visible in this view."
                    : "Choose the layer(s) with pile circles (pileLayers) and with pile cap outlines (capLayers; caps drawn "
                      + "as loose lines count too, see lineCount), then call scan_cad_piles again.",
            };
        }

        var circles = scan.SelectCircles(pileLayers, region);
        var caps = PileFoundationPlacer.BuildCapRows(scan.SelectBoundaries(capLayers, region), circles);
        AttachCapTags(document, view, caps);
        var pileTypes = PileTypeMatcher.CollectFoundationTypes(document);
        var groups = PileFoundationPlacer.GroupCircles(circles, pileTypes);
        var scanId = context.Sessions.Add(PileScan.Kind, document, new PileScan(cad.Id, view.Id, groups, caps));
        var defaultFamily = DefaultFamilies.Find(document, DefaultFamilies.PileFamilyName);
        var slabTypes = PileDefaults.CollectFoundationSlabTypes(document);

        return new
        {
            source,
            scanId,
            layers = layerInfo,
            pileCount = groups.Sum(group => group.Circles.Count),
            pilesWithoutCap = groups.Sum(group => group.Circles.Count(circle => circle.OwningBoundary is null)),
            groups = groups.Select(group =>
            {
                var exact = PileTypeMatcher.FindExactMatch(group.DiameterMm, pileTypes, s => s);
                var nearest = exact ?? PileTypeMatcher.AutoMatch(group.DiameterMm, pileTypes, s => s);
                return new
                {
                    key = group.Key,
                    diameterMm = group.DiameterMm,
                    count = group.Circles.Count,
                    withoutCap = group.Circles.Count(circle => circle.OwningBoundary is null),
                    exactType = exact is null ? null : StructuralJson.PileType(exact),
                    nearestType = exact is not null || nearest is null ? null : StructuralJson.PileType(nearest),
                };
            }).ToList(),
            capCount = caps.Count,
            capGroups = caps.GroupBy(PileScan.CapKey).Select(group => new
            {
                key = group.Key,
                count = group.Count(),
                size = PileScan.SizeKey(group.First()),
                // From the cap mark; null means the drawing does not say and the user must give the cap type.
                thicknessMm = group.Select(cap => cap.TagThicknessMm).FirstOrDefault(thickness => thickness is not null),
                markText = group.First().TagText,
            }).ToList(),
            caps = caps.Take(MaxCapsReturned).Select(cap => new
            {
                label = cap.Label,
                key = PileScan.CapKey(cap),
                size = PileScan.SizeKey(cap),
                thicknessMm = cap.TagThicknessMm,
                piles = groups.Sum(group => group.Circles.Count(circle => circle.OwningBoundary == cap)),
            }).ToList(),
            foundationSlabTypes = slabTypes.Select(StructuralJson.SlabType).ToList(),
            defaults = new
            {
                pileFamily = DefaultFamilies.PileFamilyName,
                pileFamilyId = defaultFamily is null ? null : RevitJson.Id(defaultFamily.Id),
                capTypeTemplate = PileDefaults.CapTypeName,
                lotType = PileDefaults.LotTypeName,
                capTypeTemplateExists = PileDefaults.FindByName(slabTypes, PileDefaults.CapTypeName) is not null,
            },
            next = "Review piles and caps with the user. Piles take their top elevation from the cap they sit in, so piles "
                + "without a cap (further than 300 mm from every cap) cannot be placed. Cap thickness comes from the cap "
                + "marks; for cap groups whose thicknessMm is null, ask the user for the cap type or thickness, never guess. "
                + "Then call propose_piles with this scanId, capLevelId and capTypes / capThicknessMm. If there is no pile "
                + "type with a Diameter or no cap slab type, call load_default_family kind=pile first.",
        };
    }

    // Each cap's mark: a Text Note inside its outline, else the nearest one beside it (PileCaps.PickTag).
    private static void AttachCapTags(Document document, View view, IReadOnlyList<BoundaryRow> caps)
    {
        var texts = new FilteredElementCollector(document, view.Id).OfClass(typeof(TextNote)).Cast<TextNote>()
            .Select(note => (Text: note.Text.Trim(), X: UnitConvert.Length(note.Coord.X), Y: UnitConvert.Length(note.Coord.Y)))
            .Where(text => text.Text.Length > 0)
            .ToList();
        if (texts.Count == 0) return;
        foreach (var cap in caps)
        {
            var outline = cap.Boundary.Points.Select(point => (UnitConvert.Length(point.X), UnitConvert.Length(point.Y))).ToList();
            if (PileCaps.PickTag(texts, outline) is not { } tag) continue;
            cap.TagName = tag.Name;
            cap.TagText = tag.Text;
            cap.TagThicknessMm = tag.ThicknessMm;
        }
    }
}
