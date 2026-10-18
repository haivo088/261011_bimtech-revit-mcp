using System.IO;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Cad;
using Bimtech.RevitMcp.Commands.Common;
using Bimtech.RevitMcp.Protocol;
using Bimtech.RevitMcp.Recognition;
using Bimtech.StructuralCore.Walls;

namespace Bimtech.RevitMcp.Commands.Structural;

// A wall scan kept in SessionStore; propose_walls reads it back by scanId. Walls stay raw (not grouped)
// because propose_walls may still drop unsupported walls or extend them to columns before grouping.
// FromHatch: recognized from hatches, so grouped by evidence and thickness and never merged again.
internal sealed record WallScan(ElementId CadId, ElementId ViewId, IReadOnlyList<RawWall> Walls, IReadOnlyList<string> GroupKeys, bool FromHatch = false)
{
    public const string Kind = "scan";
}

// Reads structural walls from a CAD.
//   Without "layers" (the normal way): from the hatches of the DWG file itself, read from disk, as an engineer would
//     (WallRecognizer): each hatched strip is weighed on evidence (pattern shared with the columns, concrete
//     thickness, on a grid, joined to a column, on a wall of the storey below, part of a core) and grouped by
//     thickness into sure ("w300") and to-review ("w300-maybe") groups.
//   With "layers": the older reading of parallel line pairs on those layers, for drawings without hatches.
public sealed class ScanCadWallsCommand : IRevitCommand
{
    // Revit lines read to place the DWG file (enough long lines to vote and a sample to check).
    private const int AlignmentEntities = 150_000;
    private const double GridMarginMm = 3000;

    public string Method => "scan_cad_walls";
    public CommandAccess Access => CommandAccess.Read;

    public object? Execute(CommandContext context)
    {
        var p = context.Params;
        var document = context.RequireDocument();
        var cad = StructuralJson.RequireCad(document, p.RequireElementId("cadId"), "cadId");
        var view = StructuralJson.RequirePlanView(context, document, cad);
        var layers = p.GetStringList("layers", maxCount: 50, maxLength: 255);
        var source = new { cad = StructuralJson.CadRef(document, cad), view = StructuralJson.Ref(view) };
        return layers.Count > 0
            ? FromLines(context, document, cad, view, layers, source)
            : FromHatches(context, document, cad, view, source);
    }

    private static object FromHatches(CommandContext context, Document document, ImportInstance cad, ViewPlan view, object source)
    {
        var p = context.Params;
        var level = p.Has("levelId")
            ? StructuralJson.RequireLevel(document, p.RequireElementId("levelId"), "levelId")
            : view.GenLevel ?? throw RpcException.InvalidParams("levelId", "the view has no level: pass levelId, the base level of the walls.");
        var options = new WallRecognizer.Options
        {
            GridToleranceMm = p.GetDouble("gridToleranceMm", 15, 0, 200),
            ColumnMaxLengthMm = p.GetDouble("columnMaxLengthMm", 1000, 0, 5000),
        };

        var path = p.GetString("dwgPath", 1024) ?? DwgSource.LinkedPath(document, cad)
            ?? throw RpcException.InvalidParams("dwgPath",
                "the CAD is imported, not linked, so Revit keeps no file path. Pass dwgPath, the full path of its DWG file "
                + "(walls are read from the file's hatches, which Revit does not expose).");
        if (!Path.IsPathFullyQualified(path)) throw RpcException.InvalidParams("dwgPath", "must be an absolute file path.");

        var read = DwgSource.Read(cad, path, () => CadExtractor.Extract(document, cad, view, new CadExtractor.Limits(AlignmentEntities)));
        context.Cancellation.ThrowIfCancellationRequested();
        var dwg = new
        {
            path,
            readSeconds = Math.Round(read.ReadMs / 1000.0, 1),
            cached = read.Drawing is not null && read.ReadMs == 0,
            placement = read.Alignment?.Placement,
            matchRate = read.Alignment is { } alignment ? Math.Round(alignment.MatchRate, 3) : (double?)null,
            problem = read.Problem,
        };
        if (read.Drawing is null || read.Alignment?.Placement is not { } placement || read.Problem is not null)
            return new
            {
                source,
                scanId = (string?)null,
                dwg,
                next = "The DWG file could not be placed on this CAD, so no wall was read. Check that dwgPath is the file this "
                    + "CAD was imported from, then try again; for a drawing without hatches, call again with the layers "
                    + "holding wall outlines (older line-pair reading).",
            };

        var scene = new CadCase();
        DwgAlignment.AddTo(scene, read.Drawing, placement);
        var grids = CadExtractor.ModelGrids(document);
        var frame = BeamCase.FrameOf(grids);
        var region = Region(p, grids);
        // Columns of the storey (reaching 1 m above its base) and walls of the storey below.
        var columns = CadExtractor.ModelSupports(document, level.Elevation + Feet(1000)).Where(s => s.Kind == "column").ToList();
        var below = CadExtractor.ModelWallsBelow(document, level.Elevation);
        var result = WallRecognizer.Recognize(scene, frame, columns, below, region.Value, options);
        context.Cancellation.ThrowIfCancellationRequested();

        var walls = result.Walls.Select(ToRaw).ToList();
        var groups = WallPlacer.GroupRecognizedWalls(walls);
        var types = WallPlacer.CollectBasicWallTypes(document);
        var scanId = walls.Count == 0 ? null : context.Sessions.Add(WallScan.Kind, document,
            new WallScan(cad.Id, view.Id, walls, groups.Select(group => group.Key).ToList(), FromHatch: true));

        return new
        {
            source,
            scanId,
            dwg,
            level = StructuralJson.Ref(level),
            region = region.Value is { } r ? new { minX = r.MinX, minY = r.MinY, maxX = r.MaxX, maxY = r.MaxY } : null,
            regionFrom = region.From,
            model = new { grids = grids.Count, columns = columns.Count, wallsBelow = below.Count },
            hatchesInRegion = result.HatchesInRegion,
            hatchesSkipped = result.Skipped,
            columnPatterns = result.ColumnPatterns,
            columnPatternsFrom = result.ColumnPatternsFromModel
                ? "hatches inside the modeled columns"
                : "squarish hatches (no hatch inside a modeled column)",
            wallCount = walls.Count,
            groups = groups.Select(group =>
            {
                var (suggested, isExact) = WallPlacer.SuggestType(group.SizeBMm, types, type => type);
                return new
                {
                    key = group.Key,
                    sure = group.Sure,
                    thicknessMm = group.SizeBMm,
                    count = group.Walls.Count,
                    modelByDefault = group.ModelByDefault,
                    evidence = Evidence(group.Walls.Select(wall => wall.Evidence ?? [])),
                    exactType = isExact && suggested is not null ? StructuralJson.WallTypeRef(suggested) : null,
                    nearestType = !isExact && suggested is not null ? StructuralJson.WallTypeRef(suggested) : null,
                };
            }).ToList(),
            dropped = new { count = result.Dropped.Count, evidence = Evidence(result.Dropped.Select(wall => wall.Reasons)) },
            next = walls.Count == 0
                ? "No wall found in the hatches of this region. Check the region (it defaults to the grids' extent) and that "
                    + "the walls are hatched in this drawing."
                : "Show the user the groups with their evidence. \"wNNN\" groups (enough evidence) are proposed by default; "
                    + "\"wNNN-maybe\" groups only when listed in 'include'. Then call propose_walls with this scanId and the base "
                    + "and top levels. Groups without exactType get a new type \"BTC-VachKC_<thickness>\".",
        };
    }

    // The region to read: 'region' if given, else the extent of the model's grids with a margin (one DWG often holds
    // several plans side by side), else the whole file.
    private static (WallRecognizer.Region? Value, string From) Region(CommandParams p, List<TruthGrid> grids)
    {
        if (StructuralJson.ReadRegion(p.GetObject("region")) is { } region)
            return (new WallRecognizer.Region(Mm(region.MinX), Mm(region.MinY), Mm(region.MaxX), Mm(region.MaxY)), "region");
        if (grids.Count == 0) return (null, "whole file (no grids in the model)");
        var xs = grids.SelectMany(g => new[] { g.X0, g.X1 }).ToList();
        var ys = grids.SelectMany(g => new[] { g.Y0, g.Y1 }).ToList();
        return (new WallRecognizer.Region(xs.Min() - GridMarginMm, ys.Min() - GridMarginMm, xs.Max() + GridMarginMm, ys.Max() + GridMarginMm),
            "grids");
    }

    private static RawWall ToRaw(WallRecognizer.Wall wall)
    {
        var (start, end) = (new XYZ(Feet(wall.X0), Feet(wall.Y0), 0), new XYZ(Feet(wall.X1), Feet(wall.Y1), 0));
        var half = new XYZ(-(end - start).Y, (end - start).X, 0).Normalize() * Feet(wall.ThicknessMm / 2);
        return new RawWall
        {
            Start = start,
            End = end,
            WidthFeet = Feet(wall.ThicknessMm),
            Segments = [(start + half, end + half), (start - half, end - half)],
            Sure = wall.Sure,
            Evidence = wall.Reasons,
            Percent = wall.Percent,
            Grid = wall.Grid,
        };
    }

    // How many walls carry each piece of evidence, most common first: {"+1 nằm trên trục": 12, ...}.
    internal static Dictionary<string, int> Evidence(IEnumerable<IEnumerable<string>> reasons) =>
        reasons.SelectMany(list => list).GroupBy(reason => reason).OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key, g => g.Count());

    // Older reading: pairs of parallel lines on the chosen layers, names from nearby Text Notes.
    private static object FromLines(CommandContext context, Document document, ImportInstance cad, ViewPlan view, IReadOnlyList<string> layers, object source)
    {
        var p = context.Params;
        var region = StructuralJson.ReadRegion(p.GetObject("region"));
        var textSearchMm = p.GetDouble("textSearchMm", WallScanPipeline.DefaultTextSearchExpandMm, 1, 20000);
        var scan = WallScanPipeline.ScanWithTags(document, cad, view, Feet(textSearchMm),
            WallScanPipeline.DefaultMinOverlapFraction, WallScanPipeline.DefaultMinPairLengthRatio);
        context.Cancellation.ThrowIfCancellationRequested();

        var missing = layers.Where(layer => !scan.WallsByLayer.ContainsKey(layer)).ToList();
        if (missing.Count > 0)
            throw RpcException.InvalidParams("layers", $"no walls found on layer(s): {string.Join(", ", missing)}.");
        var layerInfo = layers
            .Select(layer => new { name = layer, wallCount = scan.WallsByLayer.TryGetValue(layer, out var list) ? list.Count : 0 })
            .ToList();

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
                + "and top levels.",
        };
    }

    private static double Feet(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
    private static double Mm(double feet) => Math.Round(UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters), 1);
}
