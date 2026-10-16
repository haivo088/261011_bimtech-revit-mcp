using System.IO;
using System.Text.RegularExpressions;
using Bimtech.RevitMcp.Cad;
using Bimtech.RevitMcp.Commands;
using Bimtech.RevitMcp.Commands.Common;
using Bimtech.RevitMcp.Commands.Structural;
using Bimtech.RevitMcp.Protocol;
using Bimtech.RevitMcp.Recognition;

namespace Bimtech.RevitMcp.DevTools;

// Saves one CAD import as a CadCase (cases/<caseId>/case.json, gitignored) so recognition rules can be developed
// and regression-tested outside Revit. With includeTruth, the grids already in the model are stored as the truth:
// export after the grids were built and approved, so they are known to be right. For the level (levelId, else the
// view's), the columns and walls reaching it are saved as beam supports and its beams as the beam truth, so
// BeamCase.Run replays propose_beams on the case.
public sealed partial class ExportCadCaseCommand : IRevitCommand
{
    public string Method => "export_cad_case";
    // Writes a file to the case folder, never to the model.
    public CommandAccess Access => CommandAccess.Read;

    public object? Execute(CommandContext context)
    {
        var p = context.Params;
        var document = context.RequireDocument();
        var cad = StructuralJson.RequireCad(document, p.RequireElementId("cadId"), "cadId");
        var view = StructuralJson.RequirePlanView(context, document, cad);
        var caseRoot = p.RequireString("caseRoot", 1024);
        if (!Path.IsPathFullyQualified(caseRoot))
            throw RpcException.InvalidParams("caseRoot", "must be an absolute folder path.");
        var maxEntities = p.GetInt("maxEntities", 300_000, 1, 2_000_000);

        // The level whose columns and walls (beam supports) and beams are saved: levelId, else the plan view's level.
        var level = p.Has("levelId")
            ? StructuralJson.RequireLevel(document, p.RequireElementId("levelId"), "levelId")
            : view.GenLevel;

        var data = CadExtractor.Extract(document, cad, view, new CadExtractor.Limits(maxEntities));
        data.Notes = p.GetString("notes", 2000);
        var dwg = ReadDwg(p, document, cad, data);
        if (level is not null)
            data.Model = new CaseModel
            {
                Level = level.Name,
                ElevationMm = Math.Round(UnitConvert.Length(level.Elevation), 2),
                Supports = CadExtractor.ModelSupports(document, level.Elevation),
            };
        if (p.GetBool("includeTruth", true))
            data.Truth = new CadTruth
            {
                Grids = CadExtractor.ModelGrids(document),
                Beams = level is null ? [] : CadExtractor.ModelBeams(document, level),
            };
        data.CaseId = $"{data.CreatedAt:yyyyMMdd-HHmmss}-{SafeName(data.Source.CadName)}";

        var folder = Path.Combine(caseRoot, data.CaseId);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "case.json");
        data.Save(path);

        return new
        {
            caseId = data.CaseId,
            path,
            sizeKb = new FileInfo(path).Length / 1024,
            counts = new
            {
                styles = data.Styles.Count,
                lines = data.Lines.Count,
                arcs = data.Arcs.Count(arc => !arc.IsCircle),
                circles = data.Arcs.Count(arc => arc.IsCircle),
                polylines = data.Polylines.Count,
                hatches = data.Hatches.Count,
                texts = data.Texts.Count,
                skipped = data.Skipped,
            },
            dwg,
            level = data.Model?.Level,
            supports = new
            {
                columns = data.Model?.Supports.Count(s => s.Kind == "column") ?? 0,
                walls = data.Model?.Supports.Count(s => s.Kind == "wall") ?? 0,
            },
            truthGrids = data.Truth?.Grids.Count,
            truthBeams = data.Truth?.Beams.Count,
            note = (data.Truth is { Grids.Count: > 0 }
                    ? "Saved with the model's grids as truth. Check they are the approved grids of this drawing. "
                    : "Saved without truth grids. Build and approve the grids, then export again to make it a regression case. ")
                + (data.Truth is { Beams.Count: > 0 }
                    ? "The beams on this level are saved as truth for beam recognition: export after they were checked and fixed."
                    : "No beams on this level: export again after the beams are built and checked to test beam recognition."),
        };
    }

    // The DWG file itself, for the hatches and texts Revit does not expose: a linked CAD's own file, else dwgPath.
    // Added to the case only when the file, placed by matching lines, reproduces the CAD Revit shows.
    private static object ReadDwg(CommandParams p, Autodesk.Revit.DB.Document document, Autodesk.Revit.DB.ImportInstance cad, CadCase data)
    {
        if (!p.GetBool("readDwg", true)) return new { read = false, reason = "readDwg is false." };
        var path = p.GetString("dwgPath", 1024) ?? DwgSource.LinkedPath(document, cad);
        if (path is null)
            return new
            {
                read = false,
                reason = "The CAD is imported, not linked, so Revit keeps no file path: pass dwgPath to read its hatches and texts.",
            };
        if (!Path.IsPathFullyQualified(path)) throw RpcException.InvalidParams("dwgPath", "must be an absolute file path.");

        var result = DwgSource.Read(cad, path, data);
        var attached = DwgSource.Attach(data, result);
        var drawing = result.Drawing;
        return new
        {
            read = drawing is not null,
            attached,
            path,
            version = result.Version,
            units = result.Units,
            readSeconds = Math.Round(result.ReadMs / 1000.0, 1),
            placement = result.Alignment?.Placement,
            checkedLines = result.Alignment?.Checked,
            matchedLines = result.Alignment?.Matched,
            matchRate = result.Alignment is { } alignment ? Math.Round(alignment.MatchRate, 3) : (double?)null,
            problem = result.Problem,
            hatches = drawing?.Hatches.Count,
            texts = drawing?.Texts.Count,
            // What the hatches look like, to judge them as wall and column evidence: by layer and pattern.
            hatchesByLayer = drawing?.Hatches
                .GroupBy(hatch => (drawing.Styles[hatch.Style].Layer, hatch.Pattern))
                .OrderByDescending(group => group.Count()).Take(20)
                .Select(group => new { layer = group.Key.Layer, pattern = group.Key.Pattern, count = group.Count() })
                .ToList(),
            skippedInFile = drawing?.Skipped,
        };
    }

    // File-system-safe and short: letters, digits, '-' and '_' only.
    private static string SafeName(string name)
    {
        var safe = UnsafeChars().Replace(Path.GetFileNameWithoutExtension(name), "_").Trim('_');
        return safe.Length == 0 ? "cad" : safe.Length > 60 ? safe[..60] : safe;
    }

    [GeneratedRegex(@"[^A-Za-z0-9_-]+")]
    private static partial Regex UnsafeChars();
}
