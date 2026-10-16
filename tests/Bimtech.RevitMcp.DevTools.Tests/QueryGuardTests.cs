using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using Bimtech.RevitMcp.DevTools;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Bimtech.RevitMcp.DevTools.Tests;

// Compiles snippets the way run_revit_query does (guards inserted), runs them for real, and checks that endless
// code stops at the time budget while normal code still gives the same answer.
public sealed class QueryGuardTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(300);

    private static readonly MetadataReference[] References =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Append(typeof(QueryGuard).Assembly.Location)
            .Distinct()
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToArray();

    private static object? RunGuarded(string body)
    {
        var source = "using System; using System.Linq; using System.Collections.Generic;\n"
            + "public static class Query { public static object Run() {\n" + body + "\n} }";
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create("Q" + Guid.NewGuid().ToString("N"), [tree], References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        compilation = compilation.ReplaceSyntaxTree(tree, QueryGuardInserter.Insert(tree, compilation.GetSemanticModel(tree)));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        stream.Position = 0;
        var run = new AssemblyLoadContext(null, isCollectible: true).LoadFromStream(stream)
            .GetType("Query")!.GetMethod("Run")!;

        QueryGuard.Begin(Budget, CancellationToken.None);
        try
        {
            return run.Invoke(null, null);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
        finally
        {
            QueryGuard.End();
        }
    }

    [Theory]
    [InlineData("while (true) { }")]
    [InlineData("for (;;) ;")]
    [InlineData("do { } while (true);")]
    [InlineData("var list = new List<int> { 1 }; foreach (var x in list.ToList()) { while (x > 0) { } } return null;")]
    [InlineData("IEnumerable<int> Forever() { var i = 0; while (true) yield return i++; } return Forever().Count();")]
    [InlineData("Func<int> spin = () => { while (true) { } }; return spin();")]
    [InlineData("while (true) { try { var x = 1; } catch (Exception) { } }")]
    [InlineData("try { while (true) { } } catch (Exception) { } while (true) { }")]
    public void Endless_code_is_stopped_at_the_time_budget(string body)
    {
        var watch = Stopwatch.StartNew();

        Assert.Throws<QueryTimeoutException>(() => RunGuarded(body));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }

    // Endless recursion would overflow the stack and kill the process (Revit), which no catch can stop.
    [Theory]
    [InlineData("int Down(int n) => Down(n + 1) - 1; return Down(0);")]
    [InlineData("int Down(int n) { return Down(n + 1); } return Down(0);")]
    [InlineData("Func<int, int> f = null; f = x => f(x + 1); return f(0);")]
    [InlineData("Func<int, int> f = null; f = delegate (int x) { return f(x + 1); }; return f(0);")]
    [InlineData("Action a = null; a = () => a(); a(); return null;")]
    public void Endless_recursion_is_stopped_before_the_stack_overflows(string body)
    {
        Assert.Throws<QueryDepthException>(() => RunGuarded(body));
    }

    [Fact]
    public void Recursion_within_the_depth_limit_still_works()
    {
        Assert.Equal(500, RunGuarded("int Count(int n) => n == 0 ? 0 : 1 + Count(n - 1); return Count(500);"));
    }

    [Fact]
    public void Expression_tree_lambdas_are_left_alone()
    {
        var result = RunGuarded("""
            System.Linq.Expressions.Expression<Func<int, int>> e = x => x + 1;
            return e.Compile()(41);
            """);

        Assert.Equal(42, result);
    }

    [Fact]
    public void Normal_code_gives_the_same_answer_with_guards()
    {
        var result = RunGuarded("""
            var total = 0;
            for (var i = 1; i <= 10; i++) total += i;
            foreach (var (a, b) in new[] { (1, 2), (3, 4) }) total += a * b;
            var n = 3;
            do { total++; } while (--n > 0);
            int Square(int x) => x * x;
            void Add(int x) => total += x;
            Add(Square(4));
            Func<int, int> twice = x => { return 2 * x; };
            return new[] { 1, 2, 3 }.Select(x => twice(x)).Sum() + total;
            """);

        // 55 + (2 + 12) + 3 + 16 = 88, plus 2 + 4 + 6 = 12.
        Assert.Equal(100, result);
    }

    [Fact]
    public void Ticks_outside_a_query_do_nothing()
    {
        QueryGuard.Tick();
    }
}
