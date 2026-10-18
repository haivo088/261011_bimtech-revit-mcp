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
        Assert.Contains("+20% " + ColumnRecognizer.CrossingReason, column.Reasons);
        Assert.Contains("+20% " + ColumnRecognizer.HatchReason, column.Reasons);
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
    public void A_lone_rectangle_off_the_grids_is_only_to_review()
    {
        var result = Recognize([Outline(3000, 3000, 600, 300, layer: "A-FURN")]);

        var lone = Assert.Single(result.Columns);
        Assert.False(lone.Sure);
        Assert.Equal(35, lone.Percent);
        Assert.Contains("-10% " + ColumnRecognizer.AloneReason, lone.Reasons);
        Assert.Contains("-5% " + ColumnRecognizer.OtherLayerReason, lone.Reasons);
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
            column => Assert.Contains(Evidence.VetoReason(ColumnRecognizer.WallRunReason), column.Reasons));
    }

    [Fact]
    public void Columns_at_two_neighbouring_crossings_are_not_a_wall_cut_by_a_door()
    {
        // Grid 8 and grids A, A' 1800 apart: a 350 x 1400 column at A-8 and a 330 x 750 one at A'-8, one line, 1050 apart.
        var frame = BeamCase.FrameOf(
        [
            new TruthGrid { Name = "8", X0 = 47400, Y0 = -5000, X1 = 47400, Y1 = 5000 },
            new TruthGrid { Name = "A", X0 = 40000, Y0 = 0, X1 = 55000, Y1 = 0 },
            new TruthGrid { Name = "A'", X0 = 40000, Y0 = -1800, X1 = 55000, Y1 = -1800 },
        ]);
        Shape[] shapes =
        [
            new("01 Colum", 47375, 700, 350, 1400, 0, false), new("HATCH", 47375, 700, 350, 1400, 0, true),
            new("BAO", 47365, -1425, 330, 750, 0, false), new("HATCH", 47365, -1425, 330, 750, 0, true),
        ];

        var result = ColumnRecognizer.Recognize(shapes, frame, [], [], []);

        Assert.Equal(2, result.Columns.Count);
        Assert.All(result.Columns, column =>
        {
            Assert.True(column.Sure);
            Assert.DoesNotContain(Evidence.VetoReason(ColumnRecognizer.WallRunReason), column.Reasons);
        });
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
        Assert.Contains("+10% " + ColumnRecognizer.TagReason, column.Reasons);
        Assert.Contains("+20% " + ColumnRecognizer.BelowReason, column.Reasons);
    }

    [Fact]
    public void Repeated_sizes_on_one_grid_are_sure_without_hatch()
    {
        // A 400 x 400 column on grid 1 between A and B, and two more of that size at crossings.
        var result = Recognize([Outline(0, 3000), Outline(6000, 0), Outline(12000, 0)]);

        var midSpan = result.Columns.Single(column => column.Cy == 3000);
        Assert.Equal("trục 1", midSpan.Grid);
        Assert.Equal(70, midSpan.Percent);        Assert.True(midSpan.Sure);
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
    // A 150 x 150 square on grid 1, drawn as a polyline, with or without its two diagonals as loose lines.
    private static CadCase Square(bool crossed)
    {
        var data = new CadCase { Styles = [new CadStyle { Layer = "DCE_BaoBeTong" }, new CadStyle { Layer = "DCE_Dim" }] };
        data.Polylines.Add(new CadPolyline { Style = 0, Closed = true, Points = [[-75, 2925], [75, 2925], [75, 3075], [-75, 3075]] });
        if (crossed)
        {
            data.Lines.Add(new CadLine { Style = 1, X0 = -75, Y0 = 2925, X1 = 75, Y1 = 3075 });
            data.Lines.Add(new CadLine { Style = 1, X0 = 75, Y0 = 2925, X1 = -75, Y1 = 3075 });
        }
        return data;
    }

    [Fact]
    public void A_rectangle_with_both_diagonals_is_found_crossed()
    {
        Assert.True(Assert.Single(ColumnRecognizer.FindShapes(Square(crossed: true), null)).Crossed);
        Assert.False(Assert.Single(ColumnRecognizer.FindShapes(Square(crossed: false), null)).Crossed);
    }

    [Fact]
    public void One_diagonal_is_not_an_x()
    {
        var data = Square(crossed: true);
        data.Lines.RemoveAt(1);

        Assert.False(Assert.Single(ColumnRecognizer.FindShapes(data, null)).Crossed);
    }

    [Fact]
    public void An_empty_crossed_rectangle_is_a_floor_opening_to_review_not_a_sure_column()
    {
        // Repeated on grids, which alone would make it sure; the drawing fills its three columns.
        var result = Recognize([.. FilledColumns, Outline(0, 3000, 150, 150) with { Crossed = true },
            Outline(6000, 3000, 150, 150) with { Crossed = true }, Outline(12000, 3000, 150, 150) with { Crossed = true }]);

        Assert.All(result.Columns.Where(column => column.SizeA == 150), column =>
        {
            Assert.False(column.Sure);
            Assert.Equal(Evidence.VetoCapPercent, column.Percent);
            Assert.Contains(Evidence.VetoReason(ColumnRecognizer.OpeningReason), column.Reasons);
        });
        Assert.Empty(result.Dropped);
    }

    // Three filled 400 x 400 columns with an X on grid A: the drawing fills its crossed columns.
    private static readonly Shape[] FilledColumns =
    [
        Outline(0, 0) with { Crossed = true }, Hatch(0, 0), Outline(6000, 0) with { Crossed = true }, Hatch(6000, 0),
        Outline(12000, 0) with { Crossed = true }, Hatch(12000, 0),
    ];

    [Fact]
    public void Without_filled_columns_an_empty_x_says_nothing()
    {
        // No DWG read, or a drawing that fills only its plain columns: an X may be a column's own sign.
        var result = Recognize([Outline(0, 0), Hatch(0, 0), Outline(6000, 0), Hatch(6000, 0), Outline(12000, 0), Hatch(12000, 0),
            Outline(0, 3000) with { Crossed = true }, Outline(6000, 3000) with { Crossed = true },
            Outline(12000, 3000) with { Crossed = true }]);

        Assert.All(result.Columns, column => Assert.True(column.Sure));
    }

    [Fact]
    public void A_filled_column_drawn_with_an_x_stays_a_column()
    {
        var result = Recognize(FilledColumns);

        var column = result.Columns.Single(column => column.Cx == 0);
        Assert.True(column.Sure);
        Assert.DoesNotContain(column.Reasons, reason => reason.Contains(ColumnRecognizer.OpeningReason));
    }

    [Fact]
    public void A_crossed_column_on_the_column_below_stays_a_column()
    {
        var below = new CaseSupport
        {
            Kind = "column", Cx = 0, Cy = 3000, Points = [[-200, 2800], [200, 2800], [200, 3200], [-200, 3200]],
        };
        var result = Recognize([.. FilledColumns, Outline(0, 3000) with { Crossed = true }], [below]);

        var column = result.Columns.Single(column => column.Cy == 3000);
        Assert.True(column.Sure);
        Assert.DoesNotContain(column.Reasons, reason => reason.Contains(ColumnRecognizer.OpeningReason));
    }
}
