using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands.Common;
using Bimtech.RevitMcp.Protocol;
using Bimtech.StructuralCore.Families;
using Bimtech.StructuralCore.Piles;

namespace Bimtech.RevitMcp.Commands.Structural;

// A pile scan kept in SessionStore; place_piles reads it back by scanId.
internal sealed record PileScan(ElementId CadId, ElementId ViewId, IReadOnlyList<PileSizeGroup> Groups, IReadOnlyList<BoundaryRow> Caps)
{
    public const string Kind = "scan";
}

// Reads piles (circles) and pile caps (closed outlines) from a CAD in two passes:
//   1. without layers: CAD layers with their circle and outline counts;
//   2. with pileLayers and/or capLayers: piles grouped by diameter, caps (outlines drawn twice merged),
//      each pile assigned to the cap containing it, and a scanId for place_piles.
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
        var missingCaps = capLayers.Where(layer => !scan.BoundariesByLayer.ContainsKey(layer)).ToList();
        if (missingCaps.Count > 0)
            throw RpcException.InvalidParams("capLayers",
                $"no closed outlines found on layer(s): {string.Join(", ", missingCaps)}. Call without layers to list the layers.");

        var summaries = scan.GetLayerSummaries();
        var layerInfo = summaries
            .Take(MaxLayersReturned)
            .Select(layer => new
            {
                name = layer.Name,
                circleCount = layer.CircleCount,
                outlineCount = layer.BoundaryCount,
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
                    ? "No circles or closed outlines found. Check that the CAD layers are visible in this view."
                    : "Choose the layer(s) with pile circles (pileLayers) and with pile cap outlines (capLayers), "
                      + "then call scan_cad_piles again.",
            };
        }

        var circles = scan.SelectCircles(pileLayers, region);
        var caps = PileFoundationPlacer.BuildCapRows(scan.SelectBoundaries(capLayers, region), circles);
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
            caps = caps.Take(MaxCapsReturned).Select(cap => new
            {
                label = cap.Label,
                lengthMm = Math.Round(UnitUtils.ConvertFromInternalUnits(cap.Boundary.LengthFeet, UnitTypeId.Millimeters)),
                widthMm = Math.Round(UnitUtils.ConvertFromInternalUnits(cap.Boundary.WidthFeet, UnitTypeId.Millimeters)),
                piles = groups.Sum(group => group.Circles.Count(circle => circle.OwningBoundary == cap)),
            }).ToList(),
            foundationSlabTypes = slabTypes.Select(StructuralJson.SlabType).ToList(),
            defaults = new
            {
                pileFamily = DefaultFamilies.PileFamilyName,
                pileFamilyId = defaultFamily is null ? null : RevitJson.Id(defaultFamily.Id),
                capType = PileDefaults.CapTypeName,
                lotType = PileDefaults.LotTypeName,
                capTypeExists = PileDefaults.FindByName(slabTypes, PileDefaults.CapTypeName) is not null,
            },
            next = "Review piles and caps with the user. Piles take their top elevation from the cap they sit in, so piles "
                + "without a cap cannot be placed. Then call place_piles with this scanId, capLevelId and dryRun=true. "
                + "If there is no pile type with a Diameter or no cap slab type, call load_default_family kind=pile first.",
        };
    }
}
