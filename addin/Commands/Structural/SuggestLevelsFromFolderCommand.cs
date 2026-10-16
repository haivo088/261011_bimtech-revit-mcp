using System.IO;
using Bimtech.RevitMcp.Protocol;
using Bimtech.RevitMcp.Recognition;

namespace Bimtech.RevitMcp.Commands.Structural;

// Proposes the storey table for propose_levels from the DWG file names in a folder, e.g.
// "KC-03 MB TANG 2 (+3.600).dwg" → Tầng 2 at 3600 mm (StoreyNames). Reads file names only, never the model or the
// drawings, and invents nothing: storeys whose name or elevation the file name does not give are reported as missing,
// for Claude to ask the user.
public sealed class SuggestLevelsFromFolderCommand : IRevitCommand
{
    public string Method => "suggest_levels_from_folder";
    public CommandAccess Access => CommandAccess.Read;

    private const int MaxFiles = 500;

    public object? Execute(CommandContext context)
    {
        var folder = context.Params.RequireString("folder", 1024).Trim();
        if (!Path.IsPathFullyQualified(folder))
            throw RpcException.InvalidParams("folder", "must be an absolute folder path.");
        if (!Directory.Exists(folder))
            throw RpcException.InvalidParams("folder", "does not exist or cannot be read.");
        var option = context.Params.GetBool("recursive", false) ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        var files = Directory.EnumerateFiles(folder, "*.dwg", option).Take(MaxFiles + 1).ToList();
        if (files.Count > MaxFiles)
            throw RpcException.InvalidParams("folder", $"has more than {MaxFiles} DWG files. Choose the folder of the structural plans.");

        var (storeys, skipped, warnings) = StoreyNames.SuggestTable(files.Select(path => Path.GetRelativePath(folder, path)));
        return new
        {
            folder,
            dwgFiles = files.Count,
            storeys = storeys.Select(storey => new
            {
                name = storey.Name,
                elevationMm = storey.ElevationMm,
                cadFile = Path.Combine(folder, storey.File),
                notes = storey.Notes,
            }),
            notPlans = skipped.Select(file => new { file = file.File, notes = file.Notes }),
            warnings,
            next = "Show this table to the user. Ask for every missing name or elevation and which file to use where a "
                + "storey has several, never guess them. Then call propose_levels with the confirmed table "
                + "(name, elevationMm, cadFile).",
        };
    }
}
