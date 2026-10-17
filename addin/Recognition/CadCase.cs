using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bimtech.RevitMcp.Recognition;

// A CAD drawing as plain data: what the add-in read from one CAD import, with no Revit types, so recognition
// rules can run and be tested outside Revit. Saved by export_cad_case as cases/<caseId>/case.json; every saved
// case with a Truth becomes a regression test (tests/Bimtech.RevitMcp.Recognition.Tests).
// Lengths are millimeters in model coordinates, angles degrees.
public sealed class CadCase
{
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;
    public string CaseId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public CadCaseSource Source { get; set; } = new();
    public string? Notes { get; set; }

    // Every graphics style used by the geometry; entities point to it by index.
    public List<CadStyle> Styles { get; set; } = [];
    public List<CadLine> Lines { get; set; } = [];
    public List<CadArc> Arcs { get; set; } = [];
    public List<CadPolyline> Polylines { get; set; } = [];
    // Hatches, read from the DWG file itself (DwgImport): the Revit API does not expose them.
    public List<CadHatch> Hatches { get; set; } = [];
    // Text Notes of the view, and the texts of the DWG file when it was read (those have a Style).
    public List<CadText> Texts { get; set; } = [];
    // Geometry kinds that were not exported (ellipses, splines, solids...), with their counts.
    public Dictionary<string, int> Skipped { get; set; } = [];

    // The model the drawing was read against, e.g. the columns and walls beams rest on. Null in older cases.
    public CaseModel? Model { get; set; }

    // What the drawing really contains, confirmed by a person (e.g. the grids built from it and approved).
    public CadTruth? Truth { get; set; }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        // Keeps Vietnamese layer names and texts readable in the file; it is never embedded in HTML.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static CadCase Load(string path) =>
        JsonSerializer.Deserialize<CadCase>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException($"{path} is not a CAD case.");

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
}

public sealed class CadCaseSource
{
    public string Document { get; set; } = "";
    public string CadName { get; set; } = "";
    public string? CadId { get; set; }
    public bool IsLinked { get; set; }
    public string? View { get; set; }
    public string? Level { get; set; }
    // Set when the DWG file was read from disk and placed on the model (hatches, texts).
    public CadDwgSource? Dwg { get; set; }
}

// The DWG file a case's hatches and texts came from, and where it sits in the model:
// model = rotate(Scale · drawing, RotationDeg) + (Dx, Dy), the drawing already in mm by its own units.
public sealed class CadDwgSource
{
    public string Path { get; set; } = "";
    public string? Version { get; set; }
    public string? Units { get; set; }
    public double Scale { get; set; } = 1;
    public double RotationDeg { get; set; }
    public double Dx { get; set; }
    public double Dy { get; set; }
    // Share of the Revit lines checked that the placed drawing reproduces (within 1 mm).
    public double MatchRate { get; set; }
}

// One Revit graphics style of the import: its layer and how the layer draws.
// LinePattern is null when Revit reports none; "Solid" for continuous lines.
public sealed class CadStyle
{
    public string Layer { get; set; } = "";
    public string? LinePattern { get; set; }
    public string? Color { get; set; }
    // The import's own category the layer belongs to (usually the DWG file name).
    public string? Category { get; set; }
    // True when the layer is off or frozen in the DWG file (only known for styles read from the file).
    public bool? Off { get; set; }
}

public sealed class CadLine
{
    public int Style { get; set; }
    public double X0 { get; set; }
    public double Y0 { get; set; }
    public double X1 { get; set; }
    public double Y1 { get; set; }

    [JsonIgnore] public double Length => Math.Sqrt((X1 - X0) * (X1 - X0) + (Y1 - Y0) * (Y1 - Y0));
}

// A circle when IsCircle; else an arc counterclockwise from StartDeg to EndDeg.
public sealed class CadArc
{
    public int Style { get; set; }
    public double Cx { get; set; }
    public double Cy { get; set; }
    public double R { get; set; }
    public bool IsCircle { get; set; }
    public double StartDeg { get; set; }
    public double EndDeg { get; set; }
}

// Points as [x, y]; the last point is not repeated when Closed.
public sealed class CadPolyline
{
    public int Style { get; set; }
    public bool Closed { get; set; }
    public List<double[]> Points { get; set; } = [];
}

// A hatch: its pattern and its boundary loops ([x, y] points, last point not repeated, arcs as short chords).
// The outer boundary and its holes are all loops; a point is inside the hatch when an odd number of loops enclose it.
public sealed class CadHatch
{
    public int Style { get; set; }
    public string? Pattern { get; set; }
    public bool IsSolid { get; set; }
    public List<List<double[]>> Loops { get; set; } = [];
}

public sealed class CadText
{
    // Null for a Revit Text Note; the layer's style for a text read from the DWG file.
    public int? Style { get; set; }
    public string Text { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double HeightMm { get; set; }
    public double RotationDeg { get; set; }
}

// The level a case was exported for, and the columns and walls of the model that reach it (beam supports).
public sealed class CaseModel
{
    public string? Level { get; set; }
    public double ElevationMm { get; set; }
    public List<CaseSupport> Supports { get; set; } = [];
}

// A column, wall or beam in the model: its plan outline (convex, [x, y] points) and its centre (a column's insertion
// point, a wall's or beam's mid-point). HeightMm: a beam's depth h, when its type says.
public sealed class CaseSupport
{
    public string Kind { get; set; } = "column"; // "column", "wall", "cap" or "beam"
    public double? HeightMm { get; set; }
    public string? Id { get; set; }
    public string? Mark { get; set; }
    public double Cx { get; set; }
    public double Cy { get; set; }
    public List<double[]> Points { get; set; } = [];
}

public sealed class CadTruth
{
    public List<TruthGrid> Grids { get; set; } = [];
    // The beams of the case's level, as built and approved: axis from end to end, b×h from the type when known.
    public List<TruthBeam> Beams { get; set; } = [];
}

public sealed class TruthBeam
{
    public string? Mark { get; set; }
    public double X0 { get; set; }
    public double Y0 { get; set; }
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double? BMm { get; set; }
    public double? HMm { get; set; }
}

public sealed class TruthGrid
{
    public string Name { get; set; } = "";
    public double X0 { get; set; }
    public double Y0 { get; set; }
    public double X1 { get; set; }
    public double Y1 { get; set; }
}
