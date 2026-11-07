using System.Text.Json;
using Autodesk.Revit.DB;

namespace Bimtech.RevitMcp.Planning.Actions;

// Door openings of create_walls items (from wall/1.1 data: gaps bridged into one wall). Each opening is cut, inside the
// item's Transaction, into the created wall whose centreline holds the opening:
//   "openings": [[x0, y0, x1, y1, sillMm, headMm], ...]   (mm, model coordinates; sill / head from the wall base)
internal static class WallOpenings
{
    private const double OnLineMm = 1.0;

    public static List<double[]> Read(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("openings", out var list) || list.ValueKind != JsonValueKind.Array) return [];
        return list.EnumerateArray().Select(o =>
            o.ValueKind == JsonValueKind.Array && o.GetArrayLength() == 6 && o.EnumerateArray().All(v => v.ValueKind == JsonValueKind.Number)
                ? o.EnumerateArray().Select(v => v.GetDouble()).ToArray()
                : throw new PlanActionException("An opening must be [x0, y0, x1, y1, sillMm, headMm] in mm.")).ToList();
    }

    // Returns how many were cut and why the others were not.
    public static (int Cut, List<string> Errors) Cut(Document document, IEnumerable<ElementId> wallIds, IReadOnlyList<double[]> openings, double baseZFeet)
    {
        var errors = new List<string>();
        if (openings.Count == 0) return (0, errors);
        document.Regenerate();
        var walls = wallIds.Select(document.GetElement).OfType<Wall>()
            .Select(w => (Wall: w, Line: (w.Location as LocationCurve)?.Curve as Line)).Where(w => w.Line is not null).ToList();
        var cut = 0;
        foreach (var o in openings)
        {
            var mid = new XYZ(ActionParams.Feet((o[0] + o[2]) / 2), ActionParams.Feet((o[1] + o[3]) / 2), walls.FirstOrDefault().Line?.Origin.Z ?? 0);
            var host = walls.FirstOrDefault(w => w.Line!.Distance(mid) <= ActionParams.Feet(OnLineMm)).Wall;
            if (host is null)
            {
                errors.Add($"No created wall holds the opening at ({o[0]:0}, {o[1]:0}) - ({o[2]:0}, {o[3]:0}).");
                continue;
            }
            try
            {
                document.Create.NewOpening(host,
                    new XYZ(ActionParams.Feet(o[0]), ActionParams.Feet(o[1]), baseZFeet + ActionParams.Feet(o[4])),
                    new XYZ(ActionParams.Feet(o[2]), ActionParams.Feet(o[3]), baseZFeet + ActionParams.Feet(o[5])));
                cut++;
            }
            catch (Exception exception)
            {
                errors.Add($"Opening at ({o[0]:0}, {o[1]:0}): {exception.Message}");
            }
        }
        return (cut, errors);
    }
}
