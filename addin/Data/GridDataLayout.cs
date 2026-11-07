namespace Bimtech.RevitMcp.Data;

// Places data grids in model coordinates: the data's origin (e.g. A-1) sits on the Project Base Point.
public static class GridDataLayout
{
    public sealed record ModelLine(double X0, double Y0, double X1, double Y1);

    // Revit draws a new grid's head (bubble) at the END point of its line. Lines are therefore sent from the plain
    // end to the bubble end. To be confirmed in Revit (docs/08-revit-test-plan.md); flip this if the head shows at
    // the start point.
    public const bool RevitHeadAtEnd = true;

    public static ModelLine ToModelLine(DataGrid grid, double baseXmm, double baseYmm)
    {
        // Data convention: StartMm is the bubble end when Bubble == "start"; otherwise keep the data's direction.
        var (from, to) = grid.Bubble == "start" && RevitHeadAtEnd
            ? (grid.EndMm, grid.StartMm)
            : (grid.StartMm, grid.EndMm);
        return grid.IsVertical
            ? new ModelLine(baseXmm + grid.PosMm, baseYmm + from, baseXmm + grid.PosMm, baseYmm + to)
            : new ModelLine(baseXmm + from, baseYmm + grid.PosMm, baseXmm + to, baseYmm + grid.PosMm);
    }

    // Text written to the grid's Comments so the element can be traced back to the drawing and the data file.
    public static string TraceText(GridDataSource source, DataGrid grid)
    {
        var hash = source.SrcHash is { Length: >= 8 } h ? h[..8] : source.SrcHash ?? "?";
        return string.Format(GridDataText.Trace, source.Schema, source.Src ?? "?", hash, grid.Handle ?? "?");
    }
}
