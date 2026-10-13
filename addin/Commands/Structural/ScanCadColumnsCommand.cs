using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands.Common;
using Bimtech.RevitMcp.Protocol;
using Bimtech.StructuralCore.Columns;
using Bimtech.StructuralCore.Families;

namespace Bimtech.RevitMcp.Commands.Structural;

// A column scan kept in SessionStore; place_columns reads it back by scanId.
internal sealed record ColumnScan(ElementId CadId, ElementId ViewId, IReadOnlyList<string> Layers, IReadOnlyList<ColumnSizeGroup> Groups)
{
    public const string Kind = "scan";
}

// Reads column outlines from a CAD in two passes:
//   1. without "layers": returns the CAD layers that contain rectangles, so the user can choose;
//   2. with "layers":    groups the columns by size, suggests types, and returns a scanId for place_columns.
// The geometry stays in the add-in; Claude only sees counts, sizes and type suggestions.
public sealed class ScanCadColumnsCommand : IRevitCommand
{
    private const int MaxLayersReturned = 200;

    public string Method => "scan_cad_columns";

    public object? Execute(CommandContext context)
    {
        var document = context.RequireDocument();
        var cad = StructuralJson.RequireCad(document, context.Params.RequireElementId("cadId"), "cadId");
        var view = StructuralJson.RequirePlanView(context, document, cad);
        var layers = context.Params.GetStringList("layers", maxCount: 50, maxLength: 255);
        var region = StructuralJson.ReadRegion(context.Params.GetObject("region"));

        var scan = ColumnScanner.Scan(document, cad, view);
        context.Cancellation.ThrowIfCancellationRequested();

        var missing = layers.Where(layer => !scan.ColumnsByLayer.ContainsKey(layer)).ToList();
        if (missing.Count > 0)
            throw RpcException.InvalidParams("layers",
                $"no column outlines found on layer(s): {string.Join(", ", missing)}. Call without layers to list the layers that have outlines.");

        var summaries = scan.GetLayerSummaries();
        var layerInfo = summaries
            .Take(MaxLayersReturned)
            .Select(layer => new { name = layer.Name, outlineCount = layer.ColumnCount, selected = layers.Contains(layer.Name) })
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
        var groups = ColumnPlacer.GroupColumns(columns);
        var types = ColumnTypeMatcher.CollectColumnTypes(document);
        var scanId = context.Sessions.Add(ColumnScan.Kind, document, new ColumnScan(cad.Id, view.Id, layers, groups));
        var defaultFamily = DefaultFamilies.Find(document, DefaultFamilies.ColumnFamilyName);
        var hasSizedTypes = types.Any(type => ColumnTypeMatcher.ReadLengthMm(type, ColumnTypeMatcher.ColumnBParam) is not null
            && ColumnTypeMatcher.ReadLengthMm(type, ColumnTypeMatcher.ColumnHParam) is not null);

        return new
        {
            source,
            scanId,
            layers = layerInfo,
            columnCount = columns.Count,
            groups = groups.Select(group =>
            {
                var exact = ColumnTypeMatcher.FindExactMatch(group.SizeAMm, group.SizeBMm, types, s => s);
                var nearest = exact ?? ColumnTypeMatcher.AutoMatch(group.SizeAMm, group.SizeBMm, types, s => s);
                return new
                {
                    key = group.Key,
                    sizeMm = new { a = group.SizeAMm, b = group.SizeBMm },
                    count = group.Columns.Count,
                    exactType = exact is null ? null : StructuralJson.ColumnType(exact),
                    nearestType = exact is not null || nearest is null ? null : StructuralJson.ColumnType(nearest),
                };
            }).ToList(),
            defaultColumnFamily = new
            {
                name = DefaultFamilies.ColumnFamilyName,
                familyId = defaultFamily is null ? null : RevitJson.Id(defaultFamily.Id),
            },
            next = hasSizedTypes
                ? "Review the groups. Then call place_columns with this scanId, base/top levels, and dryRun=true. "
                  + "Groups without exactType need a type in 'types' or a templateFamilyId to create one."
                : "The model has no column type with b/h parameters. Ask the user, then call load_default_column_family "
                  + "and use its familyId as templateFamilyId in place_columns.",
        };
    }
}
