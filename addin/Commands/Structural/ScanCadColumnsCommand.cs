using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands.Common;
using Bimtech.RevitMcp.Protocol;
using Bimtech.RevitMcp.Recognition;
using Bimtech.StructuralCore.Columns;
using Bimtech.StructuralCore.Families;

namespace Bimtech.RevitMcp.Commands.Structural;

// A column scan kept in SessionStore; propose_columns reads it back by scanId.
// TagMismatches: per group key, the note when the mark's size differs from the drawn outline.
internal sealed record ColumnScan(
    ElementId CadId, ElementId ViewId, IReadOnlyList<string> Layers, IReadOnlyList<ColumnSizeGroup> Groups,
    IReadOnlyDictionary<string, string> TagMismatches)
{
    public const string Kind = "scan";
}

// Reads column outlines from a CAD in two passes:
//   1. without "layers": the CAD layers that contain rectangles, so the user can choose;
//   2. with "layers":    real rectangles 100-2000 mm only (shapes of column size that are not rectangles, such as
//                        L-shaped columns or columns drawn touching a wall, are listed apart), each column's mark read
//                        from the nearest Text Note ("C1 300x500"), grouped by mark and size, with type suggestions and a
//                        scanId for propose_columns.
// The geometry stays in the add-in; Claude only sees counts, sizes and type suggestions.
public sealed class ScanCadColumnsCommand : IRevitCommand
{
    private const int MaxLayersReturned = 200;
    private const int MaxRejectedListed = 30;
    // A mark whose size differs from the drawn outline by more than this is reported.
    private const double TagSizeToleranceMm = 10;

    public string Method => "scan_cad_columns";
    public CommandAccess Access => CommandAccess.Read;

    public object? Execute(CommandContext context)
    {
        var document = context.RequireDocument();
        var cad = StructuralJson.RequireCad(document, context.Params.RequireElementId("cadId"), "cadId");
        var view = StructuralJson.RequirePlanView(context, document, cad);
        var layers = context.Params.GetStringList("layers", maxCount: 50, maxLength: 255);
        var region = StructuralJson.ReadRegion(context.Params.GetObject("region"));

        var scan = ColumnScanner.Scan(document, cad, view);
        context.Cancellation.ThrowIfCancellationRequested();

        var missing = layers.Where(layer => !scan.ColumnsByLayer.ContainsKey(layer) && !scan.RejectedByLayer.ContainsKey(layer)).ToList();
        if (missing.Count > 0)
            throw RpcException.InvalidParams("layers",
                $"no column outlines found on layer(s): {string.Join(", ", missing)}. Call without layers to list the layers that have outlines.");

        var summaries = scan.GetLayerSummaries();
        var layerInfo = summaries
            .Take(MaxLayersReturned)
            .Select(layer => new
            {
                name = layer.Name,
                outlineCount = layer.ColumnCount,
                notRectangles = layer.RejectedCount,
                selected = layers.Contains(layer.Name),
            })
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
                    ? "No rectangles found. Check that the CAD layers are visible in this view."
                    : "Choose the layers holding column outlines (often named like COT/COLUMN), then call scan_cad_columns again with layers.",
            };
        }

        var columns = scan.Select(layers, region);
        var tags = ReadTags(document, view, columns);
        var groups = ColumnPlacer.GroupColumns(columns, column => tags.GetValueOrDefault(column)?.Name);
        var types = ColumnTypeMatcher.CollectColumnTypes(document);
        var mismatches = groups
            .Select(group => (group.Key, Note: TagMismatch(group, tags)))
            .Where(entry => entry.Note is not null)
            .ToDictionary(entry => entry.Key, entry => entry.Note!);
        var scanId = context.Sessions.Add(ColumnScan.Kind, document, new ColumnScan(cad.Id, view.Id, layers, groups, mismatches));
        var defaultFamily = DefaultFamilies.Find(document, DefaultFamilies.ColumnFamilyName);
        var hasSizedTypes = types.Any(type => ColumnTypeMatcher.ReadLengthMm(type, ColumnTypeMatcher.ColumnBParam) is not null
            && ColumnTypeMatcher.ReadLengthMm(type, ColumnTypeMatcher.ColumnHParam) is not null);
        var rejected = scan.SelectRejected(layers, region);

        return new
        {
            source,
            scanId,
            layers = layerInfo,
            columnCount = columns.Count,
            taggedColumns = tags.Count,
            groups = groups.Select(group =>
            {
                var exact = ColumnTypeMatcher.FindExactMatch(group.SizeAMm, group.SizeBMm, types, s => s);
                var nearest = exact ?? ColumnTypeMatcher.AutoMatch(group.SizeAMm, group.SizeBMm, types, s => s);
                return new
                {
                    key = group.Key,
                    tagName = group.TagName,
                    sizeMm = new { b = group.SizeAMm, h = group.SizeBMm },
                    count = group.Columns.Count,
                    tagSizeMismatch = mismatches.GetValueOrDefault(group.Key),
                    exactType = exact is null ? null : StructuralJson.ColumnType(exact),
                    nearestType = exact is not null || nearest is null ? null : StructuralJson.ColumnType(nearest),
                };
            }).ToList(),
            notRectangles = new
            {
                count = rejected.Count,
                shapes = rejected.Take(MaxRejectedListed).Select(shape => new
                {
                    x = UnitConvert.Length(shape.Center.X),
                    y = UnitConvert.Length(shape.Center.Y),
                    sizeMm = $"{Math.Round(shape.LengthMm)}x{Math.Round(shape.WidthMm)}",
                    shape.Reason,
                }),
            },
            defaultColumnFamily = new
            {
                name = DefaultFamilies.ColumnFamilyName,
                familyId = defaultFamily is null ? null : RevitJson.Id(defaultFamily.Id),
            },
            next = (rejected.Count > 0
                    ? $"Tell the user about the {rejected.Count} shape(s) that are not rectangles: they are not placed. "
                    : "")
                + (hasSizedTypes
                    ? "Review the groups, then call propose_columns with this scanId and base/top levels. Groups without "
                      + "exactType need a type in 'types' or a templateFamilyId to create one."
                    : "The model has no column type with b/h parameters. Ask the user, then call load_default_family "
                      + "with kind=column and use its familyId as templateFamilyId in propose_columns."),
        };
    }

    // The mark of each column: each Text Note goes to its nearest column within 1500 mm (ColumnTags.AssignNearest).
    private static Dictionary<RawColumn, ColumnTag> ReadTags(Document document, View view, IReadOnlyList<RawColumn> columns)
    {
        var texts = new FilteredElementCollector(document, view.Id).OfClass(typeof(TextNote)).Cast<TextNote>()
            .Select(note => (Text: note.Text.Trim(), X: UnitConvert.Length(note.Coord.X), Y: UnitConvert.Length(note.Coord.Y)))
            .Where(text => text.Text.Length > 0)
            .ToList();
        if (texts.Count == 0 || columns.Count == 0) return [];
        var centers = columns.Select(column => (UnitConvert.Length(column.Center.X), UnitConvert.Length(column.Center.Y))).ToList();
        var tags = ColumnTags.AssignNearest(centers, texts, ColumnTags.Parse, ColumnTags.MaxDistanceMm);
        return columns.Zip(tags).Where(pair => pair.Second is not null).ToDictionary(pair => pair.First, pair => pair.Second!);
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
}
