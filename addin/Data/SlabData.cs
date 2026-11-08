namespace Bimtech.RevitMcp.Data;

// Clean slab data produced outside Revit (schema "slab/1.0", read from the drawing through InnoCAD). Plain records, no
// Revit types. Coordinates are millimeters from the grid origin (e.g. A-1), the same origin as grid/1.0.

// One slab: outer loop, thickness, top offset from the level, the ids of its openings, and (slab/1.1) its inner loops:
// the openings and the zone slabs inside it. Zone "main" is the main slab; else the hatch pattern of its zone.
public sealed record DataSlab(
    string Id,
    string Zone,
    double Thickness,
    double TopOffset,
    IReadOnlyList<(double X, double Y)> Points,
    IReadOnlyList<string> OpeningIds,
    string GridRef,
    IReadOnlyList<IReadOnlyList<(double X, double Y)>>? Holes = null);

// An opening cut through a slab (a region marked with an X). Kind is for reading only: "lift", "duct", "stair".
// Flags: "on_edge" (touches the slab edge: a notch, not a hole), "holes".
public sealed record DataSlabOpening(
    string Id,
    string? Slab,
    string Kind,
    double AreaM2,
    IReadOnlyList<(double X, double Y)> Points,
    string GridRef,
    IReadOnlyList<string> Flags)
{
    // A hole Revit can cut: not touching the slab edge.
    public bool CanCut => !Flags.Contains("on_edge");
}

public sealed record SlabDataSource(
    string Schema,
    string? Tool,
    string? Source,
    string? Drawing,
    string? Src,
    string? SrcHash,
    string? Origin,
    string? GridFile,
    string? GridHash,
    string? LevelBase);

public sealed record SlabDataSet(
    SlabDataSource Source,
    IReadOnlyList<DataSlab> Slabs,
    IReadOnlyList<DataSlabOpening> Openings,
    IReadOnlyList<string> Issues,
    IReadOnlyList<string> Decisions)
{
    public IReadOnlyList<DataSlabOpening> OpeningsOf(DataSlab slab) =>
        Openings.Where(o => slab.OpeningIds.Contains(o.Id)).ToList();
}
