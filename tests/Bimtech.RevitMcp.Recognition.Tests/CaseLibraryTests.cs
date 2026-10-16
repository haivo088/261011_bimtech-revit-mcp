using Bimtech.RevitMcp.Recognition;
using Xunit;
using Xunit.Abstractions;

namespace Bimtech.RevitMcp.Recognition.Tests;

// Every case saved with export_cad_case that has truth grids must be recognized exactly. The cases live in
// cases/ at the repository root (gitignored, only on the developer's machine) or in BIMTECH_CASES_DIR, so on
// other machines this test finds none and passes.
public sealed class CaseLibraryTests(ITestOutputHelper output)
{
    [Fact]
    public void Every_saved_case_with_truth_grids_is_recognized_exactly()
    {
        var failures = new List<string>();
        var checkedCases = 0;
        foreach (var path in CasePaths())
        {
            var data = CadCase.Load(path);
            if (data.Truth is not { Grids.Count: > 0 }) continue;
            checkedCases++;
            var comparison = GridComparison.Compare(GridRecognizer.Recognize(data).Grids, data.Truth.Grids);
            output.WriteLine($"{data.CaseId}: {comparison.Matched}/{comparison.TruthCount} grids matched");
            if (!comparison.IsPerfect)
                failures.Add($"{data.CaseId}: wrong names [{string.Join(", ", comparison.WrongNames)}], "
                    + $"missing [{string.Join(", ", comparison.Missing)}], extra [{string.Join(", ", comparison.Extra)}]");
        }
        output.WriteLine($"{checkedCases} case(s) with truth grids checked.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    // Every case saved with its level's supports and approved beams must give back exactly those beams.
    [Fact]
    public void Every_saved_case_with_truth_beams_is_recognized_exactly()
    {
        var failures = new List<string>();
        var checkedCases = 0;
        foreach (var path in CasePaths())
        {
            var data = CadCase.Load(path);
            if (data.Model is null || data.Truth is not { Beams.Count: > 0 }) continue;
            checkedCases++;
            var result = BeamCase.Run(data);
            output.WriteLine($"{data.CaseId}: {result.Matched}/{result.TruthCount} beams matched, {result.Extra.Count} extra, "
                + $"{result.Recognition.Dangling.Count} dangling");
            if (!result.IsPerfect)
                failures.Add($"{data.CaseId}: missing [{string.Join("; ", result.Missing)}], extra [{string.Join("; ", result.Extra)}]");
        }
        output.WriteLine($"{checkedCases} case(s) with truth beams checked.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static IEnumerable<string> CasePaths()
    {
        var root = Environment.GetEnvironmentVariable("BIMTECH_CASES_DIR") ?? FindRepositoryCases();
        return root is not null && Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "case.json", SearchOption.AllDirectories).Order()
            : [];
    }

    // cases/ next to Bimtech.RevitMcp.sln, found by walking up from the test binaries.
    private static string? FindRepositoryCases()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "Bimtech.RevitMcp.sln")))
                return Path.Combine(folder.FullName, "cases");
        return null;
    }
}
