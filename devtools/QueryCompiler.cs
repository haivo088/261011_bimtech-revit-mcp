using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Bimtech.RevitMcp.Commands;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Bimtech.RevitMcp.DevTools;

// Compiles a run_revit_query script into an in-memory assembly. The assembly loads into the dev tools'
// AssemblyLoadContext, so it sees the same Revit and add-in types. Each run leaves a small assembly in memory
// until Revit closes; acceptable for a developer tool.
internal static class QueryCompiler
{
    private const string Prefix =
        "using System;\nusing System.Collections.Generic;\nusing System.Linq;\nusing System.Text;\n"
        + "using Autodesk.Revit.DB;\nusing Autodesk.Revit.DB.Structure;\nusing Autodesk.Revit.UI;\n"
        + "using Bimtech.RevitMcp.DevTools;\n"
        + "namespace BimtechDevQuery {\npublic static class Query {\npublic static object? Run(QueryContext ctx) {\n"
        + "var doc = ctx.Doc; var uidoc = ctx.UiDoc; var view = ctx.View;\n";
    private const string Suffix = "\n#line hidden\nreturn null;\n}\n}\n}\n";
    private static readonly int PrefixLines = Prefix.Count(c => c == '\n');

    private static readonly Lazy<IReadOnlyList<MetadataReference>> References = new(LoadReferences);

    internal sealed record Diagnostic(int Line, int Column, string Id, string Message);

    internal sealed record Result(Func<QueryContext, object?>? Run, IReadOnlyList<Diagnostic> Errors);

    public static Result Compile(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(Prefix + code + Suffix,
            new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create(
            "BimtechDevQuery_" + Guid.NewGuid().ToString("N"),
            [tree],
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Disable));

        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        if (!emitted.Success)
            return new Result(null, emitted.Diagnostics
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .Take(20)
                .Select(diagnostic =>
                {
                    var position = diagnostic.Location.GetLineSpan().StartLinePosition;
                    return new Diagnostic(position.Line + 1 - PrefixLines, position.Character + 1, diagnostic.Id,
                        diagnostic.GetMessage());
                })
                .ToList());

        stream.Position = 0;
        var context = AssemblyLoadContext.GetLoadContext(typeof(QueryCompiler).Assembly) ?? AssemblyLoadContext.Default;
        var assembly = context.LoadFromStream(stream);
        var method = assembly.GetType("BimtechDevQuery.Query")!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;
        return new Result(method.CreateDelegate<Func<QueryContext, object?>>(), []);
    }

    // The .NET runtime's assemblies plus Revit, the add-in and this DLL, one file per assembly name.
    private static IReadOnlyList<MetadataReference> LoadReferences()
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trusted)
            foreach (var path in trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                paths.TryAdd(Path.GetFileNameWithoutExtension(path), path);
        foreach (var assembly in new[]
                 {
                     typeof(object).Assembly, typeof(Enumerable).Assembly, typeof(Document).Assembly,
                     typeof(UIApplication).Assembly, typeof(IRevitCommand).Assembly, typeof(QueryContext).Assembly,
                 })
            if (!string.IsNullOrEmpty(assembly.Location))
                paths[assembly.GetName().Name!] = assembly.Location;

        return paths.Values
            .Where(File.Exists)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
    }
}
