using System.Globalization;

namespace Bimtech.RevitMcp.Recognition;

// Grid positions from imprecise drawings. Spacings drawn as 5998.7 mean 6000: each spacing between neighbouring
// axes is snapped to the nearest multiple of a module when it is within a tolerance, and the axes are placed by
// adding up the snapped spacings from the first axis, so errors do not pile up across bays.
// Also finds where the axes of one storey's drawing sit in the grid of another, so each drawing can be moved by
// its own vector even when the storeys were drawn at different places.
public static class GridLayout
{
    public const double DefaultModuleMm = 50;
    public const double DefaultToleranceMm = 10;
    // Two drawings show the same axis when their relative positions agree this closely.
    public const double MatchToleranceMm = 20;

    // One axis position: its offset from the first axis after snapping, and what happened to the spacing before it.
    public sealed record Placed(double OffsetMm, double MeasuredSpacingMm, double SpacingMm, string? Note);

    // coordinates: axis positions in mm, sorted ascending. moduleMm = 0 only rounds spacings to whole mm.
    public static List<Placed> Snap(IReadOnlyList<double> coordinates, double moduleMm, double toleranceMm)
    {
        var placed = new List<Placed>(coordinates.Count);
        var offset = 0.0;
        for (var i = 0; i < coordinates.Count; i++)
        {
            if (i == 0)
            {
                placed.Add(new Placed(0, 0, 0, null));
                continue;
            }
            var measured = coordinates[i] - coordinates[i - 1];
            var rounded = Math.Round(measured, MidpointRounding.AwayFromZero);
            var multiple = moduleMm > 0 ? Math.Round(measured / moduleMm, MidpointRounding.AwayFromZero) * moduleMm : rounded;
            string? note = null;
            double spacing;
            if (moduleMm <= 0)
                spacing = rounded;
            else if (Math.Abs(measured - multiple) <= toleranceMm)
            {
                spacing = multiple;
                if (Math.Abs(measured - multiple) >= 0.5) note = $"Nhịp đo {Mm(measured)} mm → {Mm(spacing)} mm.";
            }
            else
            {
                spacing = rounded;
                note = $"Nhịp đo {Mm(measured)} mm không gần bội {Mm(moduleMm)} mm (lệch quá {Mm(toleranceMm)} mm): giữ {Mm(spacing)} mm.";
            }
            offset += spacing;
            placed.Add(new Placed(offset, measured, spacing, note));
        }
        return placed;
    }

    // Index in `reference` of the axis that `other`'s first axis is, when every axis of `other` lines up with an axis
    // of `reference` (relative positions within MatchToleranceMm). Null when no position fits, or several do (a
    // regular grid where the drawing could be shifted by whole bays), since guessing would misplace the drawing.
    public static int? Match(IReadOnlyList<double> reference, IReadOnlyList<double> other)
    {
        if (other.Count == 0 || other.Count > reference.Count) return null;
        var fits = new List<int>();
        for (var start = 0; start + other.Count <= reference.Count; start++)
        {
            var allFit = true;
            for (var i = 0; i < other.Count && allFit; i++)
            {
                var expected = reference[start + i] - reference[start];
                var actual = other[i] - other[0];
                allFit = Math.Abs(expected - actual) <= MatchToleranceMm;
            }
            if (allFit) fits.Add(start);
        }
        return fits.Count == 1 ? fits[0] : null;
    }

    private static string Mm(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
