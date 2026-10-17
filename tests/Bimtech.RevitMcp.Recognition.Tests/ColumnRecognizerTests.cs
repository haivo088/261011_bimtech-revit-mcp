using Bimtech.RevitMcp.Recognition;
using Xunit;
using Shape = Bimtech.RevitMcp.Recognition.ColumnRecognizer.Shape;

namespace Bimtech.RevitMcp.Recognition.Tests;

// Columns are weighed on evidence from every layer, never picked by layer name alone.
public sealed class ColumnRecognizerTests
{
    // Grids 1, 2, 3 at x = 0, 6000, 12000 and A, B at y = 0, 6000.
    private static readonly BeamCase.Frame Frame = BeamCase.FrameOf(
    [
        new TruthGrid { Name = "1", X0 = 0, Y0 = -1000, X1 = 0, Y1 = 7000 },
        new TruthGrid { Name = "2", X0 = 6000, Y0 = -1000, X1 = 6000, Y1 = 7000 },
        new TruthGrid { Name = "3", X0 = 12000, Y0 = -1000, X1 = 12000, Y1 = 7000 },
        new TruthGrid { Name = "A", X0 = -1000, Y0 = 0, X1 = 13000, Y1 = 0 },
        new TruthGrid { Name = "B", X0 = -1000, Y0 = 6000, X1 = 13000, Y1 = 6000 },
    ]);

    private static Shape Outline(double x, double y, double length = 400, double width = 400, string layer = "0") =>
        new(layer, x, y, length, width, 0, false);

    private static Shape Hatch(double x, double y, double length = 400, double width = 400) =>
        new("HATCH", x, y, length, width, 0, true);

    private static ColumnRecognizer.Result Recognize(IReadOnlyList<Shape> shapes, IReadOnlyList<CaseSupport>? below = null,
        IReadOnlyList<(string, double, double)>? texts = null) =>
        ColumnRecognizer.Recognize(shapes, Frame, texts ?? [], below ?? [], []);

    [Fact]
    public void Hatched_rectangles_at_crossings_are_sure_on_any_layer()
    {
        var result = Recognize([Outline(0, 0, layer: "Layer1"), Hatch(0, 0), Outline(6000, 0, layer: "Layer1"), Hatch(6000, 0)]);

        Assert.Equal(2, result.Columns.Count);
        var column = result.Columns[0];
        Assert.True(column.Sure);
        Assert.Equal("A-1", column.Grid);
        Assert.Contains("+2 " + ColumnRecognizer.CrossingReason, column.Reasons);
        Assert.Contains("+2 " + ColumnRecognizer.HatchReason, column.Reasons);
    }

    [Fact]
    public void An_outline_and_its_hatch_are_one_column()
    {
        var result = Recognize([Outline(0, 0, layer: "COT"), Outline(5, 0, layer: "KC"), Hatch(0, 0)]);

        var column = Assert.Single(result.Columns);
        Assert.True(column.HasHatch);
        Assert.Equal(["COT", "KC", "HATCH"], column.Layers);
    }

    [Fact]
    public void A_hatch_without_outline_is_not_a_column()
    {
        var result = Recognize([Hatch(0, 0)]);

        Assert.Empty(result.Columns);
        Assert.Equal(1, result.HatchOnly);
    }

    [Fact]
    public void A_lone_rectangle_off_the_grids_is_dropped()
    {
        var result = Recognize([Outline(3000, 3000, 600, 300, layer: "A-FURN")]);

        Assert.Empty(result.Columns);
        var dropped = Assert.Single(result.Dropped);
        Assert.Contains("-1 " + ColumnRecognizer.AloneReason, dropped.Reasons);
        Assert.Contains("-1 " + ColumnRecognizer.OtherLayerReason, dropped.Reasons);
    }

    [Fact]
    public void Pieces_of_a_wall_cut_by_doors_are_not_sure_columns()
    {
        // 300 x 1100 pieces along grid 2, a 1100 mm door apart, all hatched.
        var shapes = new List<Shape>();
        foreach (var y in new[] { 550.0, 2750, 4950 })
        {
            shapes.Add(new Shape("KC", 6000, y, 300, 1100, 0, false));
            shapes.Add(new Shape("HATCH", 6000, y, 300, 1100, 0, true));
        }
        var result = Recognize(shapes);

        Assert.DoesNotContain(result.Columns, column => column.Sure);
        Assert.All(result.Columns.Concat(result.Dropped),
            column => Assert.Contains("-3 " + ColumnRecognizer.WallRunReason, column.Reasons));
    }

    [Fact]
    public void A_column_mark_and_a_column_below_add_evidence()
    {
        var below = new CaseSupport
        {
            Kind = "column", Cx = 3000, Cy = 3000,
            Points = [[2800, 2800], [3200, 2800], [3200, 3200], [2800, 3200]],
        };
        var result = Recognize([Outline(3000, 3000)], [below], [("C1 400x400", 3300, 3500)]);

        var column = Assert.Single(result.Columns);
        Assert.True(column.Sure);
        Assert.Equal("C1", column.Tag?.Name);
        Assert.Contains("+1 " + ColumnRecognizer.TagReason, column.Reasons);
        Assert.Contains("+2 " + ColumnRecognizer.BelowReason, column.Reasons);
    }

    [Fact]
    public void Repeated_sizes_on_one_grid_are_sure_without_hatch()
    {
        // A 400 x 400 column on grid 1 between A and B, and two more of that size at crossings.
        var result = Recognize([Outline(0, 3000), Outline(6000, 0), Outline(12000, 0)]);

        var midSpan = result.Columns.Single(column => column.Cy == 3000);
        Assert.Equal("trục 1", midSpan.Grid);
        Assert.Equal(2, midSpan.Score);
        Assert.False(midSpan.Sure);
        Assert.All(result.Columns.Where(column => column.Cy == 0), column => Assert.True(column.Sure));
    }

    [Fact]
    public void Fit_finds_the_smallest_rotated_rectangle()
    {
        var angle = 30 * Math.PI / 180;
        var (cos, sin) = (Math.Cos(angle), Math.Sin(angle));
        var corners = new[] { (-300.0, -150.0), (300.0, -150.0), (300.0, 150.0), (-300.0, 150.0) }
            .Select(p => (1000 + p.Item1 * cos - p.Item2 * sin, 2000 + p.Item1 * sin + p.Item2 * cos)).ToList();

        var rect = ColumnRecognizer.Fit(corners)!;

        Assert.Equal(1000, rect.Cx, 6);
        Assert.Equal(2000, rect.Cy, 6);
        Assert.Equal(180000, rect.Length * rect.Width, 3);
        Assert.InRange(rect.AngleRadians, 0, Math.PI / 2);
    }
}
