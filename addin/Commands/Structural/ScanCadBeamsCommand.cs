using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands.Common;
using Bimtech.RevitMcp.Protocol;
using Bimtech.StructuralCore.Beams;
using Bimtech.StructuralCore.Families;

namespace Bimtech.RevitMcp.Commands.Structural;

// A beam scan kept in SessionStore; place_beams reads it back by scanId.
internal sealed record BeamScan(ElementId CadId, ElementId ViewId, IReadOnlyList<BeamSizeGroup> Groups)
{
    public const string Kind = "scan";
}

// Reads beams from a CAD in two passes, like scan_cad_columns:
//   1. without "layers": CAD layers on which pairs of parallel lines were found;
//   2. with "layers":    beams grouped by the name/size read from nearby Text Notes ("D2-X7(220x600)"),
//                        or by width when no text is found, plus a scanId for place_beams.
// Optionally extends beam ends to the nearest columns on levelId, like the WPF tool's "Tự nối dầm đến cột".
public sealed class ScanCadBeamsCommand : IRevitCommand
{
    private const int MaxLayersReturned = 200;

    public string Method => "scan_cad_beams";
    public CommandAccess Access => CommandAccess.Read;

    public object? Execute(CommandContext context)
    {
        var p = context.Params;
        var document = context.RequireDocument();
        var cad = StructuralJson.RequireCad(document, p.RequireElementId("cadId"), "cadId");
        var view = StructuralJson.RequirePlanView(context, document, cad);
        var layers = p.GetStringList("layers", maxCount: 50, maxLength: 255);
        var region = StructuralJson.ReadRegion(p.GetObject("region"));
        var textSearchMm = p.GetDouble("textSearchMm", BeamScanPipeline.DefaultTextSearchExpandMm, 1, 20000);
        var extendToColumns = p.GetBool("extendToColumns", false);
        var columnSearchMm = p.GetDouble("columnSearchMm", BeamScanPipeline.DefaultColumnSearchDistanceMm, 1, 20000);
        var level = p.Has("levelId") ? StructuralJson.RequireLevel(document, p.RequireElementId("levelId"), "levelId") : null;
        if (extendToColumns && level is null)
            throw RpcException.InvalidParams("levelId", "is required when extendToColumns is true (columns are searched on this level).");

        var scan = BeamScanPipeline.ScanWithTags(document, cad, view, Feet(textSearchMm));
        context.Cancellation.ThrowIfCancellationRequested();

        var missing = layers.Where(layer => !scan.BeamsByLayer.ContainsKey(layer)).ToList();
        if (missing.Count > 0)
            throw RpcException.InvalidParams("layers",
                $"no beams found on layer(s): {string.Join(", ", missing)}. Call without layers to list the layers that have beams.");

        var summaries = scan.GetLayerSummaries();
        var layerInfo = summaries
            .Take(MaxLayersReturned)
            .Select(layer => new { name = layer.Name, beamCount = layer.Count, selected = layers.Contains(layer.Name) })
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
                    : "Choose the layers holding beam outlines (often named like DAM/BEAM), then call scan_cad_beams again with layers.",
            };
        }

        var beams = scan.Select(layers, region);
        var extended = 0;
        if (extendToColumns && level is not null)
        {
            var extension = BeamColumnExtender.Extend(document, beams, level.Elevation, Feet(columnSearchMm));
            beams = extension.Beams;
            extended = extension.ExtendedBeamCount;
        }

        var groups = BeamPlacer.GroupBeams(beams);
        var types = BeamTypeMatcher.CollectBeamTypes(document);
        var scanId = context.Sessions.Add(BeamScan.Kind, document, new BeamScan(cad.Id, view.Id, groups));
        var defaultFamily = DefaultFamilies.Find(document, DefaultFamilies.BeamFamilyName);

        return new
        {
            source,
            scanId,
            layers = layerInfo,
            beamCount = beams.Count,
            taggedBeamCount = beams.Count(beam => beam.TagName is not null),
            extendedToColumns = extended,
            groups = groups.Select(group =>
            {
                var (suggested, isExact) = BeamPlacer.SuggestType(group, types, s => s);
                return new
                {
                    key = group.Key,
                    tagName = group.TagName,
                    sizeMm = new { b = group.SizeBMm, h = group.SizeHMm },
                    count = group.Beams.Count,
                    repairedFromOrphanLines = group.Beams.Count(beam => beam.IsCrossLayerPair),
                    modelByDefault = group.ModelByDefault,
                    exactType = isExact && suggested is not null ? StructuralJson.BeamType(suggested) : null,
                    nearestType = !isExact && suggested is not null ? StructuralJson.BeamType(suggested) : null,
                };
            }).ToList(),
            defaultBeamFamily = new
            {
                name = DefaultFamilies.BeamFamilyName,
                familyId = defaultFamily is null ? null : RevitJson.Id(defaultFamily.Id),
            },
            next = "Review the groups with the user. Groups with a tag name are placed by default; groups without one "
                + "(key like \"220x600\" or \"b220\") only when listed in 'include'. Then call place_beams with this scanId, "
                + "levelId and dryRun=true. Groups without exactType need a type in 'types' or a templateFamilyId "
                + "(load_default_family kind=beam) to create one; width-only groups (\"b220\") always need 'types'.",
        };
    }

    private static double Feet(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
}
