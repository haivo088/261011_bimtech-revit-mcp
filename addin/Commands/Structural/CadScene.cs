using System.IO;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Cad;
using Bimtech.RevitMcp.Commands.Common;
using Bimtech.RevitMcp.Protocol;
using Bimtech.RevitMcp.Recognition;

namespace Bimtech.RevitMcp.Commands.Structural;

// What the scan commands that read a CAD like an engineer share: the CAD as Revit reports it plus the DWG file's
// hatches and texts, and the region to read.
internal static class CadScene
{
    private const int MaxEntities = 300_000;
    private const double GridMarginMm = 3000;

    // The CAD's lines, arcs, polylines and the view's Text Notes, with the DWG file's hatches and texts added when the
    // file is found and placed (a linked CAD's own file, or dwgPath). Dwg is null when no file was tried.
    public static (CadCase Scene, object? Dwg) Read(CommandParams p, Document document, ImportInstance cad, View view)
    {
        var scene = CadExtractor.Extract(document, cad, view, new CadExtractor.Limits(MaxEntities));
        var path = p.GetString("dwgPath", 1024) ?? DwgSource.LinkedPath(document, cad);
        if (path is null) return (scene, null);
        if (!Path.IsPathFullyQualified(path)) throw RpcException.InvalidParams("dwgPath", "must be an absolute file path.");
        var read = DwgSource.Read(cad, path, () => scene);
        var attached = DwgSource.Attach(scene, read);
        return (scene, new
        {
            path,
            attached,
            readSeconds = Math.Round(read.ReadMs / 1000.0, 1),
            matchRate = read.Alignment is { } alignment ? Math.Round(alignment.MatchRate, 3) : (double?)null,
            problem = read.Problem,
        });
    }

    // 'region' if given, else the extent of the model's grids with a margin, else the whole CAD.
    public static (WallRecognizer.Region? Value, string From) Region(CommandParams p, List<TruthGrid> grids)
    {
        if (StructuralJson.ReadRegion(p.GetObject("region")) is { } region)
            return (new WallRecognizer.Region(Mm(region.MinX), Mm(region.MinY), Mm(region.MaxX), Mm(region.MaxY)), "region");
        if (grids.Count == 0) return (null, "whole CAD (no grids in the model)");
        var xs = grids.SelectMany(g => new[] { g.X0, g.X1 }).ToList();
        var ys = grids.SelectMany(g => new[] { g.Y0, g.Y1 }).ToList();
        return (new WallRecognizer.Region(xs.Min() - GridMarginMm, ys.Min() - GridMarginMm, xs.Max() + GridMarginMm, ys.Max() + GridMarginMm),
            "grids");
    }

    public static object? RegionJson(WallRecognizer.Region? region) =>
        region is { } r ? new { minX = r.MinX, minY = r.MinY, maxX = r.MaxX, maxY = r.MaxY } : null;

    private static double Mm(double feet) => Math.Round(UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters), 1);
}
