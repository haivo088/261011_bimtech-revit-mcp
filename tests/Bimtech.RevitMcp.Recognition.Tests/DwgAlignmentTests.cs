using Bimtech.RevitMcp.Recognition;
using Xunit;

namespace Bimtech.RevitMcp.Recognition.Tests;

public class DwgAlignmentTests
{
    // A plan of irregular lines: grids of uneven spans, column outlines and many equal hatch strokes.
    private static List<Shapes.Segment> Drawing()
    {
        var lines = new List<Shapes.Segment>();
        double[] xs = [0, 6300, 14100, 21900, 27450];
        double[] ys = [0, 7200, 15600, 21000];
        foreach (var x in xs) lines.Add(new(x, -1500, x, 22500));
        foreach (var y in ys) lines.Add(new(-1500, y, 28950, y));
        foreach (var x in xs)
            foreach (var y in ys)
            {
                lines.Add(new(x - 200, y - 250, x + 200, y - 250));
                lines.Add(new(x + 200, y - 250, x + 200, y + 250));
                lines.Add(new(x + 200, y + 250, x - 200, y + 250));
                lines.Add(new(x - 200, y + 250, x - 200, y - 250));
                for (var k = 0; k < 8; k++) lines.Add(new(x - 200 + 50 * k, y - 250, x - 150 + 50 * k, y - 200));
            }
        for (var i = 0; i < 40; i++) lines.Add(new(1000 + 137 * i, 3000 + 41 * i, 1900 + 151 * i, 3100 + 53 * i));
        return lines;
    }

    private static List<Shapes.Segment> Place(IEnumerable<Shapes.Segment> lines, DwgPlacement placement) =>
        lines.Select(s =>
        {
            var (x0, y0) = placement.Map(s.X0, s.Y0);
            var (x1, y1) = placement.Map(s.X1, s.Y1);
            return new Shapes.Segment(x0, y0, x1, y1);
        }).ToList();

    [Theory]
    [InlineData(1, 0, 12345.67, -8765.4)]
    [InlineData(1, 30, -500000, 250000)]
    [InlineData(10, 90, 1000, 2000)]
    public void Finds_the_placement_revit_used(double scale, double rotation, double dx, double dy)
    {
        var drawing = Drawing();
        var model = Place(drawing, new DwgPlacement(scale, rotation, dx, dy));

        var alignment = DwgAlignment.Find(model, drawing, rotation);

        Assert.Null(alignment.Problem);
        Assert.Equal(1, alignment.MatchRate);
        var placement = alignment.Placement!;
        Assert.Equal(scale, placement.Scale);
        Assert.Equal(rotation, placement.RotationDeg, 6);
        Assert.Equal(dx, placement.Dx, 1);
        Assert.Equal(dy, placement.Dy, 1);
    }

    [Fact]
    public void Tries_quarter_turns_when_the_import_rotation_is_not_the_files()
    {
        var drawing = Drawing();
        var model = Place(drawing, new DwgPlacement(1, 90, 300, 400));

        var alignment = DwgAlignment.Find(model, drawing, 0);

        Assert.Null(alignment.Problem);
        Assert.Equal(90, alignment.Placement!.RotationDeg, 6);
    }

    [Fact]
    public void Keeps_matching_when_revit_shows_only_part_of_the_file()
    {
        var drawing = Drawing();
        var model = Place(drawing.Where((_, i) => i % 3 != 0), new DwgPlacement(1, 0, 777, 888));

        var alignment = DwgAlignment.Find(model, drawing, 0);

        Assert.Null(alignment.Problem);
        Assert.Equal(777, alignment.Placement!.Dx, 1);
    }

    [Fact]
    public void Refuses_a_different_drawing()
    {
        var model = Place(Drawing(), new DwgPlacement(1, 0, 100, 100));
        var other = Enumerable.Range(0, 200).Select(i => new Shapes.Segment(i * 7, i * 3, i * 7 + 333 + i, i * 3 + 91)).ToList();

        var alignment = DwgAlignment.Find(model, other, 0);

        Assert.NotNull(alignment.Problem);
    }

    [Fact]
    public void Adds_placed_hatches_and_texts_with_their_layers()
    {
        var target = new CadCase { Styles = [new CadStyle { Layer = "TRUC", LinePattern = "Center" }] };
        var drawing = new CadCase
        {
            Styles = [new CadStyle { Layer = "VACH", Category = "plan.dwg" }],
            Hatches = [new CadHatch { Style = 0, Pattern = "FP_1", Loops = [[[0, 0], [300, 0], [300, 4700], [0, 4700]]] }],
            Texts = [new CadText { Style = 0, Text = "THANG MÁY", X = 100, Y = 0, HeightMm = 250, RotationDeg = 350 }],
        };

        DwgAlignment.AddTo(target, drawing, new DwgPlacement(1, 90, 1000, 0));

        Assert.Equal(2, target.Styles.Count);
        var hatch = Assert.Single(target.Hatches);
        Assert.Equal(1, hatch.Style);
        Assert.Equal([1000.0, 300.0], hatch.Loops[0][1]);
        var text = Assert.Single(target.Texts);
        Assert.Equal((1000.0, 100.0), (text.X, text.Y));
        Assert.Equal(80, text.RotationDeg);
        Assert.Equal(1, text.Style);
    }
}
