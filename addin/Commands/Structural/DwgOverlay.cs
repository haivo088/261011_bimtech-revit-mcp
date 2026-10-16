using System.IO;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Cad;
using Bimtech.RevitMcp.Protocol;
using Bimtech.RevitMcp.Recognition;

namespace Bimtech.RevitMcp.Commands.Structural;

// Texts (grid names, beam and column marks) live in the DWG, which the Revit API does not expose. Reads the file behind
// a CAD (dwgPath, or a link's own file), places it on the lines Revit reports for that CAD (data) and adds its texts,
// and its circles when asked (grid bubbles), to data. Returns what happened for the tool's result.
internal static class DwgOverlay
{
    // missing: what the tool loses without the file, e.g. "grid names are numbered, not read".
    public static object Attach(CommandContext context, Document document, ImportInstance cad, CadCase data, string missing, bool circles)
    {
        var path = context.Params.GetString("dwgPath", 1024) ?? DwgSource.LinkedPath(document, cad);
        if (path is null)
            return new { attached = false, problem = $"The CAD is imported, so Revit keeps no file path: {missing}. Pass dwgPath, "
                + "the full path of its DWG file, to read the texts from the file." };
        if (!Path.IsPathFullyQualified(path)) throw RpcException.InvalidParams("dwgPath", "must be an absolute file path.");
        var read = DwgSource.Read(cad, path, () => data);
        context.Cancellation.ThrowIfCancellationRequested();
        var attached = read.Drawing is not null && read.Alignment?.Placement is not null && read.Problem is null;
        if (attached)
        {
            var (drawing, place) = (read.Drawing!, read.Alignment!.Placement!);
            DwgAlignment.AddTo(data, drawing, place);
            if (circles)
                foreach (var circle in drawing.Arcs.Where(arc => arc.IsCircle))
                {
                    var (x, y) = place.Map(circle.Cx, circle.Cy);
                    data.Arcs.Add(new CadArc { Style = circle.Style, Cx = x, Cy = y, R = circle.R * place.Scale, IsCircle = true });
                }
        }
        return new
        {
            attached,
            path,
            readSeconds = Math.Round(read.ReadMs / 1000.0, 1),
            matchRate = read.Alignment is { } alignment ? Math.Round(alignment.MatchRate, 3) : (double?)null,
            problem = read.Problem,
        };
    }
}
