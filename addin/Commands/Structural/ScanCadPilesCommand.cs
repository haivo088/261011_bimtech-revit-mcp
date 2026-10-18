using System.Globalization;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Cad;
using Bimtech.RevitMcp.Commands.Common;
using Bimtech.RevitMcp.Protocol;
using Bimtech.RevitMcp.Recognition;
using Bimtech.StructuralCore.Families;
using Bimtech.StructuralCore.Piles;

namespace Bimtech.RevitMcp.Commands.Structural;

// A pile scan kept in SessionStore; propose_piles reads it back by scanId.
// CapEvidence: each cap's signed reasons. MaybeCaps: caps below the automatic threshold, proposed for review.
// CapPercents, PilePercents: each cap's and pile's confidence (Evidence scale). CapGrids: "B-2" or "trục A" per cap.
internal sealed record PileScan(
    ElementId CadId, ElementId ViewId, IReadOnlyList<PileSizeGroup> Groups, IReadOnlyList<BoundaryRow> Caps,
    IReadOnlyDictionary<BoundaryRow, IReadOnlyList<string>>? CapEvidence = null, IReadOnlySet<BoundaryRow>? MaybeCaps = null,
    IReadOnlyDictionary<BoundaryRow, int>? CapPercents = null, IReadOnlyDictionary<RawCircle, int>? PilePercents = null,
    IReadOnlyDictionary<BoundaryRow, string>? CapGrids = null)
{
    public const string Kind = "scan";

    // Caps are grouped by their mark ("Đ1") when the drawing has one, else by size ("2000x2000", rounded to 5 mm).
    public static string CapKey(BoundaryRow cap) => cap.TagName ?? SizeKey(cap);

    public static string SizeKey(BoundaryRow cap) => $"{Mm(cap.Boundary.LengthFeet)}x{Mm(cap.Boundary.WidthFeet)}";

    private static string Mm(double feet) =>
        PileCaps.RoundTo(UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters), 5).ToString("0", CultureInfo.InvariantCulture);
}

// Reads piles and pile caps from a CAD as an engineer would, from every layer (PileRecognizer): circles of pile size and
// closed outlines of cap size, each weighed on evidence (piles inside a cap, caps holding piles at grid crossings,
// repeated sizes, pile symbols, cap marks; grid bubbles and outlines holding whole caps never count; layer names only a
// hint). Sure and to-review piles and caps go to the scan; each pile is assigned to the cap containing it, each cap's
// mark ("Đ1 (2000x2000x1200)") gives its thickness. pileLayers/capLayers only narrow what is read.
public sealed class ScanCadPilesCommand : IRevitCommand
{
    private const int MaxCapsReturned = 100;
    private const int MaxListedLayers = 30;

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
        var source = new { cad = StructuralJson.CadRef(document, cad), view = StructuralJson.Ref(view) };

        var (scene, dwg) = CadScene.Read(p, document, cad, view);
        context.Cancellation.ThrowIfCancellationRequested();
        var grids = CadExtractor.ModelGrids(document);
        var (region, regionFrom) = CadScene.Region(p, grids);
        var result = PileRecognizer.Recognize(scene, grids, region);
        context.Cancellation.ThrowIfCancellationRequested();

        var piles = result.Piles.ToList();
        var caps = result.Caps.ToList();
        RequireLayers("pileLayers", pileLayers, piles.Concat(result.DroppedPiles).SelectMany(pile => pile.Layers));
        RequireLayers("capLayers", capLayers, caps.Concat(result.DroppedCaps).SelectMany(cap => cap.Layers));
        if (pileLayers.Count > 0) piles = piles.Where(pile => pile.Layers.Any(pileLayers.Contains)).ToList();
        if (capLayers.Count > 0) caps = caps.Where(cap => cap.Layers.Any(capLayers.Contains)).ToList();

        var boundaries = caps.ToDictionary(ToBoundary, cap => cap);
        var pilePercents = piles.ToDictionary(pile => new RawCircle
        {
            Center = new XYZ(Feet(pile.Cx), Feet(pile.Cy), 0), RadiusFeet = Feet(pile.DiameterMm / 2),
        }, pile => pile.Percent);
        var circles = pilePercents.Keys.ToList();
        var capPercents = new Dictionary<BoundaryRow, int>();
        var capGrids = new Dictionary<BoundaryRow, string>();
        var rows = PileFoundationPlacer.BuildCapRows(boundaries.Keys, circles);
        var capEvidence = new Dictionary<BoundaryRow, IReadOnlyList<string>>();
        var maybeCaps = new HashSet<BoundaryRow>();
        foreach (var row in rows)
        {
            var cap = boundaries[row.Boundary];
            capEvidence[row] = cap.Reasons;
            capPercents[row] = cap.Percent;
            if (cap.Grid is { } grid) capGrids[row] = grid;
            if (!cap.Sure) maybeCaps.Add(row);
            if (cap.Tag is { } tag)
            {
                row.TagName = tag.Name;
                row.TagText = tag.Text;
                row.TagThicknessMm = tag.ThicknessMm;
            }
        }
        var pileTypes = PileTypeMatcher.CollectFoundationTypes(document);
        var groups = PileFoundationPlacer.GroupCircles(circles, pileTypes);
        var scanId = groups.Count == 0 && rows.Count == 0 ? null : context.Sessions.Add(PileScan.Kind, document,
            new PileScan(cad.Id, view.Id, groups, rows, capEvidence, maybeCaps, capPercents, pilePercents, capGrids));
        var defaultFamily = DefaultFamilies.Find(document, DefaultFamilies.PileFamilyName);
        var slabTypes = PileDefaults.CollectFoundationSlabTypes(document);

        return new
        {
            source,
            scanId,
            dwg,
            region = CadScene.RegionJson(region),
            regionFrom,
            model = new { grids = grids.Count },
            // Where piles and caps were drawn: a hint for the person, never a filter.
            layers = new
            {
                piles = Layers(piles.SelectMany(pile => pile.Layers)),
                caps = Layers(caps.SelectMany(cap => cap.Layers)),
            },
            pileCount = groups.Sum(group => group.Circles.Count),
            surePiles = piles.Count(pile => pile.Sure),
            pilesWithoutCap = groups.Sum(group => group.Circles.Count(circle => circle.OwningBoundary is null)),
            pileEvidence = ScanCadWallsCommand.Evidence(piles.Select(pile => pile.Reasons)),
            droppedPiles = new { count = result.DroppedPiles.Count, evidence = ScanCadWallsCommand.Evidence(result.DroppedPiles.Select(pile => pile.Reasons)) },
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
            capCount = rows.Count,
            sureCaps = rows.Count - maybeCaps.Count,
            capGroups = rows.GroupBy(PileScan.CapKey).Select(group => new
            {
                key = group.Key,
                count = group.Count(),
                toReview = group.Count(maybeCaps.Contains),
                size = PileScan.SizeKey(group.First()),
                // From the cap mark; null means the drawing does not say and the user must give the cap type.
                thicknessMm = group.Select(cap => cap.TagThicknessMm).FirstOrDefault(thickness => thickness is not null),
                markText = group.First().TagText,
                evidence = ScanCadWallsCommand.Evidence(group.Select(cap => capEvidence[cap])),
            }).ToList(),
            caps = rows.Take(MaxCapsReturned).Select(cap => new
            {
                label = cap.Label,
                key = PileScan.CapKey(cap),
                size = PileScan.SizeKey(cap),
                sure = !maybeCaps.Contains(cap),
                grid = boundaries[cap.Boundary].Grid,
                thicknessMm = cap.TagThicknessMm,
                piles = groups.Sum(group => group.Circles.Count(circle => circle.OwningBoundary == cap)),
            }).ToList(),
            droppedCaps = new { count = result.DroppedCaps.Count, evidence = ScanCadWallsCommand.Evidence(result.DroppedCaps.Select(cap => cap.Reasons)) },
            foundationSlabTypes = slabTypes.Select(StructuralJson.SlabType).ToList(),
            defaults = new
            {
                pileFamily = DefaultFamilies.PileFamilyName,
                pileFamilyId = defaultFamily is null ? null : RevitJson.Id(defaultFamily.Id),
                capTypeTemplate = PileDefaults.CapTypeName,
                lotType = PileDefaults.LotTypeName,
                capTypeTemplateExists = PileDefaults.FindByName(slabTypes, PileDefaults.CapTypeName) is not null,
            },
            next = scanId is null
                ? "No pile or cap found in this region. Check the region (it defaults to the grids' extent) and that the CAD is visible in the view."
                : "Show the user the piles and caps with their evidence; caps that are not sure are marked for review. Piles "
                    + "take their top elevation from the cap they sit in, so piles without a cap (further than 300 mm from every "
                    + "cap) cannot be placed. Cap thickness comes from the cap marks; for cap groups whose thicknessMm is null, "
                    + "ask the user for the cap type or thickness, never guess. Then call propose_piles with this scanId, "
                    + "capLevelId and capTypes / capThicknessMm. If there is no pile type with a Diameter or no cap slab type, "
                    + "call load_default_family kind=pile first."
                    + (dwg is null ? " The DWG file was not read (an imported CAD keeps no path): pass dwgPath to add hatch and text evidence." : ""),
        };
    }

    private static void RequireLayers(string name, IReadOnlyList<string> wanted, IEnumerable<string> found)
    {
        var present = found.ToHashSet(StringComparer.Ordinal);
        var missing = wanted.Where(layer => !present.Contains(layer)).ToList();
        if (missing.Count > 0)
            throw RpcException.InvalidParams(name, $"nothing of that kind found on layer(s): {string.Join(", ", missing)}. Call without layers to read every layer.");
    }

    private static object Layers(IEnumerable<string> layers) =>
        layers.GroupBy(layer => layer).OrderByDescending(group => group.Count()).Take(MaxListedLayers)
            .Select(group => new { name = group.Key, count = group.Count() }).ToList();

    // A cap outline as the pile placer reads it: closed (first point repeated), with its axis-aligned box.
    private static RawBoundary ToBoundary(PileRecognizer.Cap cap)
    {
        var points = cap.Points.Select(p => new XYZ(Feet(p[0]), Feet(p[1]), 0)).Append(new XYZ(Feet(cap.Points[0][0]), Feet(cap.Points[0][1]), 0)).ToList();
        var (dx, dy) = (points.Max(q => q.X) - points.Min(q => q.X), points.Max(q => q.Y) - points.Min(q => q.Y));
        return new RawBoundary
        {
            Center = new XYZ(Feet(cap.Cx), Feet(cap.Cy), 0),
            LengthFeet = Math.Max(dx, dy),
            WidthFeet = Math.Min(dx, dy),
            Points = points,
        };
    }

    private static double Feet(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
}
