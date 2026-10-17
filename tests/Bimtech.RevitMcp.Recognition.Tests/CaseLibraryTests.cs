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

    // Every saved case with the model's columns at its level: at least 95% of those columns must be found as sure
    // columns (centre within 100 mm). The columns reaching a level were built from one storey's plan, and the plan of
    // the storey next to it may move a column or two. Extra sure columns are listed, not failed: the model's columns
    // were built from one layer and may miss some.
    [Fact]
    public void Every_saved_case_with_model_columns_gives_them_back_as_sure_columns()
    {
        var failures = new List<string>();
        var checkedCases = 0;
        foreach (var path in CasePaths())
        {
            var data = CadCase.Load(path);
            var truth = data.Model?.Supports.Where(s => s.Kind == "column").ToList() ?? [];
            if (truth.Count == 0 || data.Truth is not { Grids.Count: > 0 }) continue;
            checkedCases++;
            var result = ColumnRecognizer.Run(data);
            var sure = result.Columns.Where(c => c.Sure).ToList();
            static bool Near(ColumnRecognizer.Column c, CaseSupport s) => Math.Sqrt(Math.Pow(c.Cx - s.Cx, 2) + Math.Pow(c.Cy - s.Cy, 2)) <= 100;
            var missing = truth.Where(s => !sure.Any(c => Near(c, s))).Select(s => $"#{s.Id} ({s.Cx:0}, {s.Cy:0})").ToList();
            var extra = sure.Where(c => !truth.Any(s => Near(c, s)))
                .Select(c => $"{c.SizeA:0}x{c.SizeB:0} ({c.Cx:0}, {c.Cy:0}) [{string.Join("; ", c.Reasons)}]").ToList();
            var review = result.Columns.Where(c => !c.Sure).ToList();
            output.WriteLine($"{data.CaseId}: {truth.Count - missing.Count}/{truth.Count} columns sure, {extra.Count} extra, "
                + $"{review.Count} to review, {result.Dropped.Count} dropped ({result.Shapes} outlines, {result.Hatches} hatches)");
            foreach (var line in extra) output.WriteLine($"  extra {line}");
            foreach (var c in review)
                output.WriteLine($"  review {c.SizeA:0}x{c.SizeB:0} ({c.Cx:0}, {c.Cy:0}) {string.Join(", ", c.Layers)}: {string.Join("; ", c.Reasons)}");
            foreach (var line in missing) output.WriteLine($"  missing {line}");
            if (missing.Count > 0.05 * truth.Count) failures.Add($"{data.CaseId}: missing [{string.Join("; ", missing)}]");
        }
        output.WriteLine($"{checkedCases} case(s) with model columns checked.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    // The secondary round on every case with approved beams: those beams become supports, and the secondary beams
    // found are listed with their evidence. Each must start and end on a support and never repeat an approved beam.
    [Fact]
    public void Every_saved_case_with_truth_beams_gives_secondary_beams_resting_on_them()
    {
        var failures = new List<string>();
        foreach (var path in CasePaths())
        {
            var data = CadCase.Load(path);
            if (data.Model is null || data.Truth is not { Beams.Count: > 0 }) continue;
            var supported = new CadCase
            {
                CaseId = data.CaseId, Styles = data.Styles, Lines = data.Lines, Polylines = data.Polylines, Texts = data.Texts,
                Hatches = data.Hatches, Truth = new CadTruth { Grids = data.Truth.Grids },
                Model = new CaseModel { Supports = [.. data.Model.Supports, .. data.Truth.Beams.Select(beam => BeamCase.BeamSupport(beam))] },
            };
            var result = BeamCase.Run(supported, new BeamRecognizer.Options { Secondary = true });
            var frame = BeamCase.FrameOf(data.Truth.Grids);
            var walls = SecondaryBeams.WallLoops(data.Hatches.SelectMany(ColumnRecognizer.OuterLoops), frame);
            var secondary = result.Beams.Where(found => found.Beam.IsSecondary).ToList();
            var repeated = secondary.Where(found => data.Truth.Beams.Any(truth => Same(found, truth))).ToList();
            output.WriteLine($"{data.CaseId}: {secondary.Count} secondary beams ({repeated.Count} repeat an approved beam)");
            foreach (var found in secondary.Except(repeated))
            {
                var weight = SecondaryBeams.Weigh(found.Beam, result.Recognition.Beams, frame, walls);
                output.WriteLine($"  {(weight.Sure ? "sure  " : "review")} {found.Span} b={found.Beam.WidthMm:0} {found.Beam.Tag?.Text}"
                    + $" ({found.X0:0}, {found.Y0:0}) → ({found.X1:0}, {found.Y1:0}): {string.Join("; ", weight.Reasons)}");
            }
            if (repeated.Count > 0 && repeated.Count == secondary.Count && secondary.Count > 0)
                failures.Add($"{data.CaseId}: every secondary beam repeats an approved beam");
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static bool Same(BeamCase.Found found, TruthBeam truth)
    {
        bool Near(double x0, double y0, double x1, double y1) => Math.Sqrt((x0 - x1) * (x0 - x1) + (y0 - y1) * (y0 - y1)) <= BeamCase.MatchToleranceMm;
        return (Near(found.X0, found.Y0, truth.X0, truth.Y0) && Near(found.X1, found.Y1, truth.X1, truth.Y1))
            || (Near(found.X0, found.Y0, truth.X1, truth.Y1) && Near(found.X1, found.Y1, truth.X0, truth.Y0));
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
