using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands.Common;
using Bimtech.RevitMcp.Protocol;
using Bimtech.StructuralCore.Walls;

namespace Bimtech.RevitMcp.Commands.Structural;

// A wall scan kept in SessionStore; propose_walls reads it back by scanId. Walls stay raw (not grouped)
// because propose_walls may still drop unsupported walls or extend them to columns before grouping.
internal sealed record WallScan(ElementId CadId, ElementId ViewId, IReadOnlyList<RawWall> Walls, IReadOnlyList<string> GroupKeys)
{
    public const string Kind = "scan";
}

// Reads structural walls from a CAD in two passes, like scan_cad_beams:
//   1. without "layers": CAD layers on which pairs of parallel lines were found;
//   2. with "layers":    walls grouped by the name read from nearby Text Notes and the measured thickness,
//                        type suggestions, and a scanId for propose_walls.
public sealed class ScanCadWallsCommand : IRevitCommand
{
    private const int MaxLayersReturned = 200;

    public string Method => "scan_cad_walls";
    public CommandAccess Access => CommandAccess.Read;

    public object? Execute(CommandContext context)
    {
        var p = context.Params;
        var document = context.RequireDocument();
        var cad = StructuralJson.RequireCad(document, p.RequireElementId("cadId"), "cadId");
        var view = StructuralJson.RequirePlanView(context, document, cad);
        var layers = p.GetStringList("layers", maxCount: 50, maxLength: 255);
        var region = StructuralJson.ReadRegion(p.GetObject("region"));
        var textSearchMm = p.GetDouble("textSearchMm", WallScanPipeline.DefaultTextSearchExpandMm, 1, 20000);

        var scan = WallScanPipeline.ScanWithTags(document, cad, view, Feet(textSearchMm),
            WallScanPipeline.DefaultMinOverlapFraction, WallScanPipeline.DefaultMinPairLengthRatio);
        context.Cancellation.ThrowIfCancellationRequested();

        var missing = layers.Where(layer => !scan.WallsByLayer.ContainsKey(layer)).ToList();
        if (missing.Count > 0)
            throw RpcException.InvalidParams("layers",
                $"no walls found on layer(s): {string.Join(", ", missing)}. Call without layers to list the layers that have walls.");

        var summaries = scan.GetLayerSummaries();
        var layerInfo = summaries
            .Take(MaxLayersReturned)
            .Select(layer => new { name = layer.Name, wallCount = layer.Count, selected = layers.Contains(layer.Name) })
            .ToList();
        var source = new { cad = StructuralJson.CadRef(document, cad), view = StructuralJson.Ref(view) };

        if (layers.Count == 0)
        {
            return new
            {
                source,
                scanId = (string?)null,
                layerCount = summaries.Count,
                layers = layerInfo,
                next = summaries.Count == 0
                    ? "No pairs of parallel lines found. Check that the CAD layers are visible in this view."
                    : "Choose the layers holding structural wall outlines (often named like VACH/WALL), then call scan_cad_walls again with layers.",
            };
        }

        var walls = scan.Select(layers, region);
        var groups = WallPlacer.GroupWalls(walls);
        var types = WallPlacer.CollectBasicWallTypes(document);
        var scanId = context.Sessions.Add(WallScan.Kind, document,
            new WallScan(cad.Id, view.Id, walls, groups.Select(group => group.Key).ToList()));

        return new
        {
            source,
            scanId,
            layers = layerInfo,
            wallCount = groups.Sum(group => group.Walls.Count),
            groups = groups.Select(group =>
            {
                var (suggested, isExact) = WallPlacer.SuggestType(group.SizeBMm, types, type => type);
                return new
                {
                    key = group.Key,
                    tagName = group.TagName,
                    thicknessMm = group.SizeBMm,
                    count = group.Walls.Count,
                    repairedFromOrphanLines = group.Walls.Count(wall => wall.IsCrossLayerPair),
                    modelByDefault = group.ModelByDefault,
                    exactType = isExact && suggested is not null ? StructuralJson.WallTypeRef(suggested) : null,
                    nearestType = !isExact && suggested is not null ? StructuralJson.WallTypeRef(suggested) : null,
                };
            }).ToList(),
            next = "Review the groups with the user. Groups with a tag name are proposed by default; groups without one "
                + "(key like \"w200\") only when listed in 'include'. Then call propose_walls with this scanId and the base "
                + "and top levels. Groups without exactType get a new type \"BTC-VachKC_<thickness>\" duplicated from the "
                + "nearest Basic Wall type, unless 'types' assigns one.",
        };
    }

    private static double Feet(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
}
