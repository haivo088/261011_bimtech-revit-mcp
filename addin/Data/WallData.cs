namespace Bimtech.RevitMcp.Data;

// Clean wall data produced outside Revit (schema "wall/1.0", read from the drawing through InnoCAD). Plain records, no
// Revit types. Coordinates are millimeters from the grid origin (e.g. A-1), the same origin as grid/1.0.

// One straight wall: centreline X1,Y1 -> X2,Y2 (ends already moved to the centrelines of the walls they join).
// Flags: "sliver" (notch of an outline), "odd_thickness", "overlaps_column".
public sealed record DataWall(
    string Type,
    double Thickness,
    double X1,
    double Y1,
    double X2,
    double Y2,
    string GridStart,
    string GridEnd,
    string? Outline,
    IReadOnlyList<string> Flags,
    IReadOnlyList<DataOpening> Openings);

// A door opening cut in a wall (wall/1.1): its ends on the wall centreline, sill and head from the wall base (mm).
// Head null: the extraction had no door height (_fix.json wall_openings.head_mm) - the wall is bridged but not cut.
public sealed record DataOpening(double X1, double Y1, double X2, double Y2, double Sill, double? Head, string? Grid);

public sealed record WallDataSource(
    string Schema,
    string? Tool,
    string? Source,
    string? Drawing,
    string? Src,
    string? SrcHash,
    string? Origin,
    string? GridFile,
    string? GridHash,
    string? LevelBase,
    string? LevelTop);

public sealed record WallDataSet(
    WallDataSource Source,
    IReadOnlyList<DataWall> Walls,
    IReadOnlySet<string> BadOutlines,
    int Slivers,
    IReadOnlyList<string> Issues,
    IReadOnlyList<string> Decisions)
{
    // Outlines whose walls do not add up to their area: only their walls need a look, not the whole file.
    public bool IsChecked(DataWall wall) => wall.Outline is null || !BadOutlines.Contains(wall.Outline);

    public bool NeedsLook(DataWall wall) => wall.Flags.Count > 0 || !IsChecked(wall);
}
