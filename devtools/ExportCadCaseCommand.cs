using System.IO;
using System.Text.RegularExpressions;
using Bimtech.RevitMcp.Cad;
using Bimtech.RevitMcp.Commands;
using Bimtech.RevitMcp.Commands.Structural;
using Bimtech.RevitMcp.Protocol;
using Bimtech.RevitMcp.Recognition;

namespace Bimtech.RevitMcp.DevTools;

// Saves one CAD import as a CadCase (cases/<caseId>/case.json, gitignored) so recognition rules can be developed
// and regression-tested outside Revit. With includeTruth, the grids already in the model are stored as the truth:
// export after the grids were built and approved, so they are known to be right.
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

        var data = CadExtractor.Extract(document, cad, view, new CadExtractor.Limits(maxEntities));
        data.Notes = p.GetString("notes", 2000);
        if (p.GetBool("includeTruth", true))
            data.Truth = new CadTruth { Grids = CadExtractor.ModelGrids(document) };
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
                texts = data.Texts.Count,
                skipped = data.Skipped,
            },
            truthGrids = data.Truth?.Grids.Count,
            note = data.Truth is { Grids.Count: > 0 }
                ? "Saved with the model's grids as truth. Check they are the approved grids of this drawing."
                : "Saved without truth grids. Build and approve the grids, then export again to make it a regression case.",
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
