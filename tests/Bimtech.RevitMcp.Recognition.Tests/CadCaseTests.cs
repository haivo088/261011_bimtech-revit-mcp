using Bimtech.RevitMcp.Recognition;
using Xunit;

namespace Bimtech.RevitMcp.Recognition.Tests;

public sealed class CadCaseTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "bimtech-case-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void A_case_survives_save_and_load_with_Vietnamese_text()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "case.json");
        var original = new CadCase
        {
            CaseId = "20261016-101500-MB_MONG",
            Source = new CadCaseSource { Document = "Dự án", CadName = "MB MÓNG.dwg", Level = "Tầng 1" },
            Styles = [new CadStyle { Layer = "TRỤC", LinePattern = "Center", Color = "#FF0000" }],
            Lines = [new CadLine { Style = 0, X0 = 0, Y0 = -1000, X1 = 0, Y1 = 20000 }],
            Arcs = [new CadArc { Style = 0, Cx = 0, Cy = -1400, R = 400, IsCircle = true }],
            Polylines = [new CadPolyline { Style = 0, Closed = true, Points = [[0, 0], [300, 0], [300, 500]] }],
            Texts = [new CadText { Text = "Trục 1", X = 0, Y = -1400, HeightMm = 350 }],
            Skipped = new() { ["Solid"] = 3 },
            Truth = new CadTruth { Grids = [new TruthGrid { Name = "1", X0 = 0, Y0 = -1000, X1 = 0, Y1 = 20000 }] },
        };

        original.Save(path);
        var loaded = CadCase.Load(path);

        Assert.Equal("TRỤC", loaded.Styles[0].Layer);
        Assert.Equal("Trục 1", loaded.Texts[0].Text);
        Assert.Equal(21000, loaded.Lines[0].Length);
        Assert.True(loaded.Arcs[0].IsCircle);
        Assert.Equal([300.0, 500.0], loaded.Polylines[0].Points[2]);
        Assert.Equal(3, loaded.Skipped["Solid"]);
        Assert.Equal("1", Assert.Single(loaded.Truth!.Grids).Name);
        Assert.Contains("\"layer\":\"TRỤC\"", File.ReadAllText(path));
    }
}
