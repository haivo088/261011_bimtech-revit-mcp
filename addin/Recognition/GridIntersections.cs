using System.Globalization;

namespace Bimtech.RevitMcp.Recognition;

// Columns usually stand where two grids cross. Works in the grid's own frame (u along one grid family, v along the
// other, mm), per direction:
//   - the column is on a grid when the grid passes through it or within marginMm of its faces;
//   - on a grid, an offset within toleranceMm of 0 (centred) or of ±half the column (face on the grid) is set to
//     exactly that, since drawings are often a few mm off; any other offset is rounded to snapMm;
//   - off every grid, the offset from the nearest grid is only rounded to snapMm.
// Also lists the crossings no column reaches, for the reviewer to check (an open atrium, or a column on another layer).
public static class GridIntersections
{
    public const double DefaultMarginMm = 300;
    public const double DefaultToleranceMm = 15;

    // One grid in the frame: Position is its fixed coordinate across the grid, Start/End its extent along it.
    public sealed record Axis(string Name, double PositionMm, double StartMm, double EndMm);

    public enum Placement { Centered, FaceOnGrid, Offset, OffGrid, NoGrid }

    // Fit of a column along one direction: the nearest grid, the drawn offset from it and the offset to place at.
    public sealed record AxisFit(string? GridName, double MeasuredOffsetMm, double OffsetMm, Placement Placement)
    {
        public double ShiftMm => OffsetMm - MeasuredOffsetMm;
        public bool OnGrid => Placement is Placement.Centered or Placement.FaceOnGrid or Placement.Offset;
    }

    // A column in the frame: centre and half its extent along u and v.
    public sealed record Column(double U, double V, double HalfU, double HalfV);

    // coordinateMm: the column centre across the grids (u for grids running along v). grids: those grids.
    // snapMm = 0 keeps every offset as drawn but still tells where the column is.
    public static AxisFit Fit(
        double coordinateMm, double halfSizeMm, IReadOnlyList<Axis> grids, double snapMm,
        double marginMm = DefaultMarginMm, double toleranceMm = DefaultToleranceMm)
    {
        if (grids.Count == 0) return new AxisFit(null, 0, 0, Placement.NoGrid);
        var nearest = grids.MinBy(grid => Math.Abs(coordinateMm - grid.PositionMm))!;
        var measured = coordinateMm - nearest.PositionMm;
        var placement =
            Math.Abs(measured) > halfSizeMm + marginMm ? Placement.OffGrid
            : Math.Abs(measured) <= toleranceMm ? Placement.Centered
            : Math.Abs(Math.Abs(measured) - halfSizeMm) <= toleranceMm ? Placement.FaceOnGrid
            : Placement.Offset;
        if (snapMm <= 0) return new AxisFit(nearest.Name, measured, measured, placement);
        var offset = placement switch
        {
            Placement.Centered => 0,
            Placement.FaceOnGrid => Math.Sign(measured) * halfSizeMm,
            _ => Math.Round(measured / snapMm, MidpointRounding.AwayFromZero) * snapMm,
        };
        return new AxisFit(nearest.Name, measured, offset, placement);
    }

    // Half the extent of a length × width rectangle along a direction at angleRad from its length side.
    public static double HalfExtent(double lengthMm, double widthMm, double angleRad) =>
        (lengthMm * Math.Abs(Math.Cos(angleRad)) + widthMm * Math.Abs(Math.Sin(angleRad))) / 2;

    // "A-1": the name starting with a letter first, as grids are usually called.
    public static string Label(string first, string second)
    {
        var firstIsNumber = first.Length > 0 && char.IsDigit(first[0]);
        var secondIsNumber = second.Length > 0 && char.IsDigit(second[0]);
        return firstIsNumber && !secondIsNumber ? $"{second}-{first}" : $"{first}-{second}";
    }

    // Crossings of a grid along u with a grid along v, inside the area the columns cover, that no column reaches
    // (centre within its half size plus marginMm in both directions). Only crossings where both grids are drawn count.
    public static List<string> Empty(
        IReadOnlyList<Axis> alongU, IReadOnlyList<Axis> alongV, IReadOnlyList<Column> columns,
        double marginMm = DefaultMarginMm)
    {
        if (columns.Count == 0) return [];
        var (minU, maxU) = (columns.Min(c => c.U - c.HalfU) - marginMm, columns.Max(c => c.U + c.HalfU) + marginMm);
        var (minV, maxV) = (columns.Min(c => c.V - c.HalfV) - marginMm, columns.Max(c => c.V + c.HalfV) + marginMm);
        var empty = new List<(double U, double V, string Label)>();
        foreach (var gu in alongU)
        foreach (var gv in alongV)
        {
            var (u, v) = (gv.PositionMm, gu.PositionMm);
            if (u < minU || u > maxU || v < minV || v > maxV) continue;
            if (!Within(u, gu.StartMm, gu.EndMm) || !Within(v, gv.StartMm, gv.EndMm)) continue;
            var reached = columns.Any(c => Math.Abs(c.U - u) <= c.HalfU + marginMm && Math.Abs(c.V - v) <= c.HalfV + marginMm);
            if (!reached) empty.Add((u, v, Label(gu.Name, gv.Name)));
        }
        return empty.OrderBy(e => e.Label, StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.None))
            .Select(e => e.Label).ToList();
    }

    private static bool Within(double value, double a, double b) =>
        value >= Math.Min(a, b) - 1 && value <= Math.Max(a, b) + 1;
}
