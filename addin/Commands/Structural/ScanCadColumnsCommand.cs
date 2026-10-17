using System.IO;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Cad;
using Bimtech.RevitMcp.Commands.Common;
using Bimtech.RevitMcp.Protocol;
using Bimtech.RevitMcp.Recognition;
using Bimtech.StructuralCore.Columns;
using Bimtech.StructuralCore.Families;

namespace Bimtech.RevitMcp.Commands.Structural;

// A column scan kept in SessionStore; propose_columns reads it back by scanId.
// TagMismatches: per group key, the note when the mark's size differs from the drawn outline.
// Evidence: each column's signed reasons. MaybeKeys: groups whose columns lack enough evidence ("300x500-maybe").
internal sealed record ColumnScan(
    ElementId CadId, ElementId ViewId, IReadOnlyList<string> Layers, IReadOnlyList<ColumnSizeGroup> Groups,
    IReadOnlyDictionary<string, string> TagMismatches,
    IReadOnlyDictionary<RawColumn, IReadOnlyList<string>>? Evidence = null, IReadOnlySet<string>? MaybeKeys = null)
{
    public const string Kind = "scan";
}

// Reads columns from a CAD as an engineer would, from every layer (ColumnRecognizer): rectangles of column size drawn
// as outlines, confirmed by the DWG file's hatches when the file can be read, weighed on evidence (grid crossings,
// hatch, repeated size, marks, a column or pile cap below, layer names as a hint) and grouped by mark and size into
// sure groups ("300x500") and groups to review ("300x500-maybe"). 'layers' only narrows the outlines read.
public sealed class ScanCadColumnsCommand : IRevitCommand
{
    private const int MaxEntities = 300_000;
    private const double GridMarginMm = 3000;
    private const int MaxListedLayers = 30;
    // A mark whose size differs from the drawn outline by more than this is reported.
    private const double TagSizeToleranceMm = 10;

    public string Method => "scan_cad_columns";
    public CommandAccess Access => CommandAccess.Read;

    public object? Execute(CommandContext context)
    {
        var p = context.Params;
        var document = context.RequireDocument();
        var cad = StructuralJson.RequireCad(document, p.RequireElementId("cadId"), "cadId");
        var view = StructuralJson.RequirePlanView(context, document, cad);
        var layers = p.GetStringList("layers", maxCount: 50, maxLength: 255);
        var level = p.Has("levelId")
            ? StructuralJson.RequireLevel(document, p.RequireElementId("levelId"), "levelId")
            : view.GenLevel;
        var source = new { cad = StructuralJson.CadRef(document, cad), view = StructuralJson.Ref(view) };

        var scene = CadExtractor.Extract(document, cad, view, new CadExtractor.Limits(MaxEntities));
        context.Cancellation.ThrowIfCancellationRequested();
        var dwg = ReadDwg(p, document, cad, scene);
        context.Cancellation.ThrowIfCancellationRequested();

        var grids = CadExtractor.ModelGrids(document);
        var (region, regionFrom) = Region(p, grids);
        var shapes = ColumnRecognizer.FindShapes(scene, region);
        var missing = layers.Where(layer => !shapes.Any(shape => !shape.IsHatch && shape.Layer == layer)).ToList();
        if (missing.Count > 0)
            throw RpcException.InvalidParams("layers",
                $"no column outline found on layer(s): {string.Join(", ", missing)}. Call without layers to read every layer.");
        if (layers.Count > 0) shapes = shapes.Where(shape => shape.IsHatch || layers.Contains(shape.Layer)).ToList();

        var below = level is null ? [] : CadExtractor.ModelSupportsBelow(document, level.Elevation);
        var result = ColumnRecognizer.Recognize(shapes, BeamCase.FrameOf(grids),
            scene.Texts.Select(text => (text.Text, text.X, text.Y)).ToList(), below,
            scene.Hatches.SelectMany(ColumnRecognizer.OuterLoops).ToList());
        context.Cancellation.ThrowIfCancellationRequested();

        var raws = result.Columns.ToDictionary(column => column, ToRaw);
        var tags = result.Columns.Where(column => column.Tag is not null).ToDictionary(column => raws[column], column => column.Tag!);
        string? TagOf(RawColumn raw) => tags.GetValueOrDefault(raw)?.Name;
        var sureGroups = ColumnPlacer.GroupColumns(result.Columns.Where(c => c.Sure).Select(c => raws[c]), TagOf);
        var maybeGroups = ColumnPlacer.GroupColumns(result.Columns.Where(c => !c.Sure).Select(c => raws[c]), TagOf)
            .Select(group => new ColumnSizeGroup
            {
                SizeAMm = group.SizeAMm, SizeBMm = group.SizeBMm, Columns = group.Columns, TagName = group.TagName,
                KeyOverride = group.Key + "-maybe",
            }).ToList();
        var groups = sureGroups.Concat(maybeGroups).ToList();
        var evidence = result.Columns.ToDictionary(column => raws[column], column => (IReadOnlyList<string>)column.Reasons);
        var mismatches = groups
            .Select(group => (group.Key, Note: TagMismatch(group, tags)))
            .Where(entry => entry.Note is not null)
            .ToDictionary(entry => entry.Key, entry => entry.Note!);
        var scanId = groups.Count == 0 ? null : context.Sessions.Add(ColumnScan.Kind, document,
            new ColumnScan(cad.Id, view.Id, layers, groups, mismatches, evidence, maybeGroups.Select(group => group.Key).ToHashSet()));

        var types = ColumnTypeMatcher.CollectColumnTypes(document);
        var defaultFamily = DefaultFamilies.Find(document, DefaultFamilies.ColumnFamilyName);
        var hasSizedTypes = types.Any(type => ColumnTypeMatcher.ReadLengthMm(type, ColumnTypeMatcher.ColumnBParam) is not null
            && ColumnTypeMatcher.ReadLengthMm(type, ColumnTypeMatcher.ColumnHParam) is not null);

        return new
        {
            source,
            scanId,
            dwg,
            level = level is null ? null : StructuralJson.Ref(level),
            region = region is { } r ? new { minX = r.MinX, minY = r.MinY, maxX = r.MaxX, maxY = r.MaxY } : null,
            regionFrom,
            model = new { grids = grids.Count, supportsBelow = below.Count },
            read = new { outlines = result.Shapes, hatches = result.Hatches, hatchesWithoutOutline = result.HatchOnly },
            // Where the columns were drawn: a hint for the person, never a filter.
            layers = result.Columns.SelectMany(column => column.Layers).GroupBy(layer => layer)
                .OrderByDescending(group => group.Count()).Take(MaxListedLayers)
                .Select(group => new { name = group.Key, columns = group.Count() }).ToList(),
            columnCount = result.Columns.Count,
            sureCount = result.Columns.Count(column => column.Sure),
            taggedColumns = tags.Count,
            groups = groups.Select(group =>
            {
                var exact = ColumnTypeMatcher.FindExactMatch(group.SizeAMm, group.SizeBMm, types, s => s);
                var nearest = exact ?? ColumnTypeMatcher.AutoMatch(group.SizeAMm, group.SizeBMm, types, s => s);
                return new
                {
                    key = group.Key,
                    sure = !group.Key.EndsWith("-maybe", StringComparison.Ordinal),
                    tagName = group.TagName,
                    sizeMm = new { b = group.SizeAMm, h = group.SizeBMm },
                    count = group.Columns.Count,
                    evidence = ScanCadWallsCommand.Evidence(group.Columns.Select(column => evidence[column])),
                    tagSizeMismatch = mismatches.GetValueOrDefault(group.Key),
                    exactType = exact is null ? null : StructuralJson.ColumnType(exact),
                    nearestType = exact is not null || nearest is null ? null : StructuralJson.ColumnType(nearest),
                };
            }).ToList(),
            dropped = new { count = result.Dropped.Count, evidence = ScanCadWallsCommand.Evidence(result.Dropped.Select(column => column.Reasons)) },
            defaultColumnFamily = new
            {
                name = DefaultFamilies.ColumnFamilyName,
                familyId = defaultFamily is null ? null : RevitJson.Id(defaultFamily.Id),
            },
            next = groups.Count == 0
                ? "No column found in this region. Check the region (it defaults to the grids' extent) and that the CAD is visible in the view."
                : "Show the user the groups with their evidence: sure groups are ticked in the review window, \"-maybe\" groups are "
                    + "proposed unticked. Then call propose_columns with this scanId and the base and top levels."
                    + (hasSizedTypes ? " Groups without exactType need a type in 'types' or a templateFamilyId."
                        : " The model has no column type with b/h parameters: ask the user, then call load_default_family with "
                          + "kind=column and use its familyId as templateFamilyId.")
                    + (dwg is null ? " The DWG file was not read (an imported CAD keeps no path): pass dwgPath to add hatch evidence." : ""),
        };
    }

    // The DWG file's hatches and texts, added to the scene when the file is found and placed. Null when not tried.
    private static object? ReadDwg(CommandParams p, Document document, ImportInstance cad, CadCase scene)
    {
        var path = p.GetString("dwgPath", 1024) ?? DwgSource.LinkedPath(document, cad);
        if (path is null) return null;
        if (!Path.IsPathFullyQualified(path)) throw RpcException.InvalidParams("dwgPath", "must be an absolute file path.");
        var read = DwgSource.Read(cad, path, () => scene);
        var attached = DwgSource.Attach(scene, read);
        return new
        {
            path,
            attached,
            readSeconds = Math.Round(read.ReadMs / 1000.0, 1),
            matchRate = read.Alignment is { } alignment ? Math.Round(alignment.MatchRate, 3) : (double?)null,
            problem = read.Problem,
        };
    }

    // 'region' if given, else the extent of the model's grids with a margin, else the whole CAD.
    private static (WallRecognizer.Region? Value, string From) Region(CommandParams p, List<TruthGrid> grids)
    {
        if (StructuralJson.ReadRegion(p.GetObject("region")) is { } region)
            return (new WallRecognizer.Region(Mm(region.MinX), Mm(region.MinY), Mm(region.MaxX), Mm(region.MaxY)), "region");
        if (grids.Count == 0) return (null, "whole CAD (no grids in the model)");
        var xs = grids.SelectMany(g => new[] { g.X0, g.X1 }).ToList();
        var ys = grids.SelectMany(g => new[] { g.Y0, g.Y1 }).ToList();
        return (new WallRecognizer.Region(xs.Min() - GridMarginMm, ys.Min() - GridMarginMm, xs.Max() + GridMarginMm, ys.Max() + GridMarginMm),
            "grids");
    }

    private static RawColumn ToRaw(ColumnRecognizer.Column column)
    {
        var center = new XYZ(Feet(column.Cx), Feet(column.Cy), 0);
        var (cos, sin) = (Math.Cos(column.AngleRadians), Math.Sin(column.AngleRadians));
        var (u, v) = (new XYZ(cos, sin, 0) * Feet(column.Length / 2), new XYZ(-sin, cos, 0) * Feet(column.Width / 2));
        var corners = new[] { center - u - v, center + u - v, center + u + v, center - u + v };
        return new RawColumn
        {
            Center = center,
            LengthFeet = Feet(column.Length),
            WidthFeet = Feet(column.Width),
            AngleRadians = column.AngleRadians,
            Segments = corners.Zip(corners.Skip(1).Append(corners[0])).ToList(),
        };
    }

    // "Ký hiệu ghi 300x600, hình vẽ 300x500" when the mark's size differs from the drawn outline.
    private static string? TagMismatch(ColumnSizeGroup group, IReadOnlyDictionary<RawColumn, ColumnTag> tags)
    {
        var tag = group.Columns.Select(tags.GetValueOrDefault).FirstOrDefault(t => t?.SizeAMm is not null);
        if (tag is null) return null;
        return Math.Abs(tag.SizeAMm!.Value - group.SizeAMm) > TagSizeToleranceMm || Math.Abs(tag.SizeBMm!.Value - group.SizeBMm) > TagSizeToleranceMm
            ? $"Ký hiệu ghi {tag.SizeAMm:0}x{tag.SizeBMm:0}, hình vẽ {group.SizeAMm:0}x{group.SizeBMm:0}."
            : null;
    }

    private static double Feet(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
    private static double Mm(double feet) => Math.Round(UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters), 1);
}
