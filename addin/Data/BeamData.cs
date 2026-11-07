namespace Bimtech.RevitMcp.Data;

// Clean beam data produced outside Revit (schema "beam/1.0", read from the drawing through InnoCAD). Plain records, no
// Revit types. Coordinates are millimeters from the grid origin (e.g. A-1), the same origin as grid/1.0.

// One beam span: centreline X1,Y1 -> X2,Y2 (ends already moved to the centre of the column / wall / beam it sits on).
// B and H null: the drawing gives no size. Flags: "no_label", "no_size", "width_mismatch", "multi_label", "inner_edge",
// "small" (an opening, not a beam), "beside_beam" (a slab step, not a beam).
public sealed record DataBeam(
    string? Name,
    string Type,
    double? B,
    double? H,
    double Width,
    double X1,
    double Y1,
    double X2,
    double Y2,
    string SupportStart,
    string SupportEnd,
    string GridStart,
    string GridEnd,
    string? SizeFrom,
    IReadOnlyList<string> Flags)
{
    public bool HasSize => B is > 0 && H is > 0;

    // Strips the extraction found are not beams (openings, slab steps): never built.
    public bool IsNotBeam => Flags.Contains("small") || Flags.Contains("beside_beam");

    // Flags that ask the reviewer to look (a size problem without size never gets this far).
    public bool NeedsLook => Flags.Any(f => f is "width_mismatch" or "multi_label" or "inner_edge");
}

public sealed record BeamDataSource(
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
    double? TopOffsetMm);

public sealed record BeamDataSet(
    BeamDataSource Source,
    IReadOnlyList<DataBeam> Beams,
    IReadOnlyList<string> Issues,
    IReadOnlyList<string> Decisions)
{
    // Beams with a size: the ones Revit can build.
    public IReadOnlyList<DataBeam> Buildable => Beams.Where(b => !b.IsNotBeam && b.HasSize).ToList();

    // Beams the drawing gives no size for: reported, not built (nothing is guessed).
    public IReadOnlyList<DataBeam> Unsized => Beams.Where(b => !b.IsNotBeam && !b.HasSize).ToList();

    public int NotBeams => Beams.Count(b => b.IsNotBeam);
}
