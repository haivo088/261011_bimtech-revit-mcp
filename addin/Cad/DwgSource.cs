using System.Diagnostics;
using System.IO;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Recognition;

namespace Bimtech.RevitMcp.Cad;

// Reads the DWG file behind a CAD import or link from disk and places it on the model, so its hatches and texts
// (which the Revit API does not expose) can join what Revit reports. The placement is found by matching lines,
// not assumed, and is refused when too few of the lines Revit reports are found in the placed file.
internal static class DwgSource
{
    internal sealed record Result(CadCase? Drawing, DwgAlignment? Alignment, string Path, string? Version, string? Units, long ReadMs, string? Problem);

    // The file of a linked CAD; null for an import, which keeps no path.
    public static string? LinkedPath(Document document, ImportInstance cad)
    {
        if (!cad.IsLinked || document.GetElement(cad.GetTypeId()) is not { } type || !type.IsExternalFileReference()) return null;
        var path = type.GetExternalFileReference()?.GetAbsolutePath();
        return path is null ? null : ModelPathUtils.ConvertModelPathToUserVisiblePath(path);
    }

    // Reads the file and places it against the lines Revit reported for the same CAD (fromRevit, model mm).
    public static Result Read(ImportInstance cad, string path, CadCase fromRevit)
    {
        if (!File.Exists(path)) return new Result(null, null, path, null, null, 0, $"File not found: {path}");
        var transform = cad.GetTotalTransform();
        if (transform.BasisX.CrossProduct(transform.BasisY).Z < 0)
            return new Result(null, null, path, null, null, 0, "The CAD is mirrored in Revit; reading its file is not supported for mirrored imports.");

        var watch = Stopwatch.StartNew();
        ACadSharp.CadDocument file;
        try
        {
            file = DwgImport.Open(path);
        }
        catch (Exception exception)
        {
            return new Result(null, null, path, null, null, watch.ElapsedMilliseconds, $"Could not read the file: {exception.Message}");
        }
        var drawing = DwgImport.ToCase(file, Path.GetFileName(path));
        var (version, units) = (file.Header.Version.ToString(), file.Header.InsUnits.ToString());
        var rotation = Math.Atan2(transform.BasisX.Y, transform.BasisX.X) * 180 / Math.PI;
        var alignment = DwgAlignment.Find(DwgAlignment.Segments(fromRevit), DwgAlignment.Segments(drawing), rotation);
        return new Result(drawing, alignment, path, version, units, watch.ElapsedMilliseconds, alignment.Problem);
    }

    // Adds the placed hatches and texts to the case read from Revit, when the placement is trusted.
    public static bool Attach(CadCase target, Result result)
    {
        if (result.Drawing is null || result.Alignment is not { Placement: { } placement, Problem: null } alignment) return false;
        DwgAlignment.AddTo(target, result.Drawing, placement);
        target.Source.Dwg = new CadDwgSource
        {
            Path = result.Path, Version = result.Version, Units = result.Units,
            Scale = placement.Scale, RotationDeg = placement.RotationDeg, Dx = placement.Dx, Dy = placement.Dy,
            MatchRate = Math.Round(alignment.MatchRate, 4),
        };
        return true;
    }
}
