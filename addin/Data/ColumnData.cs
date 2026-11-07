namespace Bimtech.RevitMcp.Data;

// Clean column data produced outside Revit (schema "column/1.0"). Plain records, no Revit types.
// Coordinates are millimeters from the grid origin (e.g. A-1), the same origin as grid/1.0.

// One column. B = short side, H = long side; RotationDeg = direction of the long side vs. the X axis (0 or 90).
// Kind: "visible" (rises from this storey), "hidden" (storey below, not built here), "planted" (starts on a beam), null.
public sealed record DataColumn(
    string Type,
    string? Kind,
    double X,
    double Y,
    double B,
    double H,
    double RotationDeg,
    string GridRef,
    string? Handle,
    string? Insert)
{
    // Size along the X axis and along the Y axis of the model.
    public double SizeXmm => RotationDeg == 90 ? B : H;
    public double SizeYmm => RotationDeg == 90 ? H : B;
}

public sealed record ColumnDataSource(
    string Schema,
    string? Tool,
    string? Src,
    string? SrcHash,
    string? FixFile,
    string? Origin,
    string? GridFile,
    string? GridHash,
    string? LevelBase,
    string? LevelTop);

// Results of the checks done outside Revit.
public sealed record ColumnDataChecks(bool Rectangles, bool NoDuplicates, int DynSizeOk, int Overlaps);

public sealed record ColumnDataSet(
    ColumnDataSource Source,
    IReadOnlyList<DataColumn> Columns,
    ColumnDataChecks Checks,
    IReadOnlyList<string> EmptyCrossings,
    int WallLike,
    IReadOnlyList<string> Issues,
    IReadOnlyList<string> Decisions)
{
    // Data checked well enough to start approved: rectangles, no duplicates, every design size equals the drawing.
    public bool IsChecked => Checks.Rectangles && Checks.NoDuplicates && Checks.Overlaps == 0 && Checks.DynSizeOk == Columns.Count;
}
