namespace Bimtech.RevitMcp.Data;

// Clean grid data produced outside Revit (schema "grid/1.0", see the extraction project's SCHEMA.md).
// Plain records with no Revit types, so reading and layout are tested without Revit.

// One grid line. Positions are millimeters from the data's origin (e.g. intersection A-1).
//   Axis "X": vertical line at x = PosMm, running along y from StartMm to EndMm.
//   Axis "Y": horizontal line at y = PosMm, running along x from StartMm to EndMm.
// StartMm is the end that carries the bubble when Bubble is "start".
public sealed record DataGrid(
    string Name,
    string Axis,
    string Kind,
    double PosMm,
    double StartMm,
    double EndMm,
    string Bubble,
    string? Handle,
    string? BubbleHandle)
{
    public bool IsVertical => Axis == "X";
    public bool IsNamed => Name != GridDataReader.UnnamedMark;
}

// Where the data came from, for review reasons and traceability in the model.
public sealed record GridDataSource(
    string Schema,
    string? Tool,
    string? Created,
    string? Src,
    string? SrcHash,
    string? FixFile,
    string? Origin,
    double OriginCadX,
    double OriginCadY);

// A whole grid/1.0 file.
public sealed record GridDataSet(
    GridDataSource Source,
    IReadOnlyList<DataGrid> Grids,
    IReadOnlyList<string> Issues,
    IReadOnlyList<string> Decisions,
    bool? SelfCheck);

// A file that does not follow grid/1.0; the message says which field.
public sealed class GridDataException(string message) : Exception(message);
