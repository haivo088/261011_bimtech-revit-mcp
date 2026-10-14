using System.IO;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;

namespace Bimtech.RevitMcp.Planning;

// Decides where the plans of a model are saved:
// - next to a saved local model: <model folder>\<model name>_bimtech\plans\
// - otherwise (unsaved, cloud, central file, read-only folder):
//   %LOCALAPPDATA%\Bimtech.RevitMcp\plans\<model key>\
public static class PlanStorageLocation
{
    public static PlanDocument For(Document document)
    {
        var path = document.PathName ?? "";
        return new PlanDocument
        {
            Title = document.Title,
            Path = path,
            StorageFolder = NextToModel(document, path) ?? LocalFolder(document.Title, path),
        };
    }

    private static string? NextToModel(Document document, string path)
    {
        if (path.Length == 0 || document.IsModelInCloud || IsCentralModel(document, path)) return null;
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory)) return null;
        var folder = Path.Combine(directory, Path.GetFileNameWithoutExtension(path) + "_bimtech", "plans");
        return CanWrite(folder) ? folder : null;
    }

    // A central file usually sits on a shared drive; plans stay on this computer instead.
    private static bool IsCentralModel(Document document, string path)
    {
        if (!document.IsWorkshared) return false;
        try
        {
            var central = ModelPathUtils.ConvertModelPathToUserVisiblePath(document.GetWorksharingCentralModelPath());
            return string.Equals(Path.GetFullPath(central), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return true; } // Revit Server and unusual paths: treat as central.
    }

    private static bool CanWrite(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string LocalFolder(string title, string path)
    {
        // The hash keeps two models with the same title apart; the title keeps the folder readable.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.Length > 0 ? path : title)))[..8];
        var safeTitle = string.Concat(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Bimtech.RevitMcp", "plans", $"{safeTitle}_{hash.ToLowerInvariant()}");
    }
}
