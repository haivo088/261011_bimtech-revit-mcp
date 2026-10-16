using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Bimtech.RevitMcp.Commands;

namespace Bimtech.RevitMcp;

// Loads the developer tools (Bimtech.RevitMcp.DevTools.dll, built from devtools/) when that DLL sits next to the
// add-in. Users never get the file, so they never get the commands; a missing file is the normal case.
// The DLL loads in its own AssemblyLoadContext, so its dependencies (Roslyn) cannot clash with versions Revit or
// another add-in already loaded. The add-in and the Revit API are shared, so IRevitCommand and Revit types stay
// the same types on both sides.
internal static class DevToolsLoader
{
    public const string AssemblyFileName = "Bimtech.RevitMcp.DevTools.dll";

    private static readonly object Lock = new();
    private static IDevToolsModule? _module;
    private static bool _attempted;

    // Shown by ping, so the developer sees why the tools are missing.
    public static string Status { get; private set; } = "not installed";

    public static IReadOnlyList<IRevitCommand> CreateCommands()
    {
        lock (Lock)
        {
            if (!_attempted)
            {
                _attempted = true;
                _module = TryLoad();
            }
            return _module?.CreateCommands().ToList() ?? [];
        }
    }

    private static IDevToolsModule? TryLoad()
    {
        var folder = Path.GetDirectoryName(typeof(DevToolsLoader).Assembly.Location);
        if (string.IsNullOrEmpty(folder)) return null;
        var path = Path.Combine(folder, AssemblyFileName);
        if (!File.Exists(path)) return null;

        try
        {
            var assembly = new DevToolsLoadContext(folder).LoadFromAssemblyPath(path);
            var type = assembly.GetTypes().FirstOrDefault(type =>
                typeof(IDevToolsModule).IsAssignableFrom(type) && type is { IsAbstract: false, IsInterface: false });
            if (type is null)
            {
                Status = $"{AssemblyFileName} has no {nameof(IDevToolsModule)}";
                return null;
            }
            Status = "loaded";
            return (IDevToolsModule)Activator.CreateInstance(type)!;
        }
        catch (Exception exception)
        {
            // A broken dev build must never stop the add-in for normal use.
            Status = $"failed to load: {exception.GetType().Name}: {exception.Message}";
            return null;
        }
    }

    private sealed class DevToolsLoadContext(string folder) : AssemblyLoadContext("BIMTECH DevTools")
    {
        private static readonly Assembly AddIn = typeof(DevToolsLoader).Assembly;

        protected override Assembly? Load(AssemblyName name)
        {
            // Shared: the add-in itself (it lives in the same folder) and everything from Revit.
            if (string.Equals(name.Name, AddIn.GetName().Name, StringComparison.OrdinalIgnoreCase)) return AddIn;
            if (name.Name is null || name.Name.StartsWith("RevitAPI", StringComparison.OrdinalIgnoreCase)) return null;

            // Private: anything shipped in the add-in folder, e.g. Microsoft.CodeAnalysis.
            var path = Path.Combine(folder, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
