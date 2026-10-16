using Bimtech.RevitMcp.Recognition;
using Xunit;

namespace Bimtech.RevitMcp.Recognition.Tests;

public sealed class GridRecognizerTests
{
    // A small framing plan: axes 1-3 at x = 0, 6000, 12000 and A-B at y = 0, 8000, drawn from -1000 past the
    // last axis. Beams (hidden lines) run along the same axes and are just as long, walls are solid lines.
    private sealed class Drawing
    {
        public CadCase Case { get; } = new();

        public int Style(string layer, string? pattern)
        {
            Case.Styles.Add(new CadStyle { Layer = layer, LinePattern = pattern });
            return Case.Styles.Count - 1;
        }

        public void Line(int style, double x0, double y0, double x1, double y1) =>
            Case.Lines.Add(new CadLine { Style = style, X0 = x0, Y0 = y0, X1 = x1, Y1 = y1 });

        // Circle of radius 400 just past the line end, with the name written near its center.
        public void Bubble(int style, double cx, double cy, string? name)
        {
            Case.Arcs.Add(new CadArc { Style = style, Cx = cx, Cy = cy, R = 400, IsCircle = true });
            if (name is not null) Case.Texts.Add(new CadText { Text = name, X = cx - 100, Y = cy + 150, HeightMm = 350 });
        }

        public static Drawing Plan(string gridLayer, string? gridPattern, string?[]? verticalNames, string?[]? horizontalNames)
        {
            var drawing = new Drawing();
            var grid = drawing.Style(gridLayer, gridPattern);
            var beam = drawing.Style("KC-DAM", "Hidden");
            var wall = drawing.Style("KC-VACH", "Solid");
            double[] xs = [0, 6000, 12000];
            double[] ys = [0, 8000];
            for (var i = 0; i < xs.Length; i++)
            {
                drawing.Line(grid, xs[i], -1000, xs[i], 9000);
                if (verticalNames is not null) drawing.Bubble(grid, xs[i], -1400, verticalNames[i]);
                drawing.Line(beam, xs[i] - 110, 0, xs[i] - 110, 8000);
                drawing.Line(beam, xs[i] + 110, 0, xs[i] + 110, 8000);
            }
            for (var i = 0; i < ys.Length; i++)
            {
                drawing.Line(grid, -1000, ys[i], 13000, ys[i]);
                if (horizontalNames is not null) drawing.Bubble(grid, -1400, ys[i], horizontalNames[i]);
                drawing.Line(beam, 0, ys[i] - 110, 12000, ys[i] - 110);
                drawing.Line(beam, 0, ys[i] + 110, 12000, ys[i] + 110);
            }
            drawing.Line(wall, 3000, 2000, 3000, 6000);
            drawing.Line(wall, 3200, 2000, 3200, 6000);
            // Dimension ticks and short marks are never grids.
            for (var x = 0; x <= 12000; x += 500) drawing.Line(grid, x, -2000, x, -2300);
            return drawing;
        }
    }

    [Fact]
    public void Grid_lines_are_found_by_their_line_pattern_and_named_from_their_bubbles()
    {
        var drawing = Drawing.Plan("KC-01", "CENTER", ["1", "2", "3"], ["A", "B"]);

        var result = GridRecognizer.Recognize(drawing.Case);

        Assert.Equal([0], result.ChosenStyles);
        Assert.Equal(["1", "2", "3", "A", "B"], result.Grids.Select(grid => grid.Name));
        Assert.All(result.Grids, grid => Assert.Equal("bubble", grid.NameSource));
        Assert.Equal([0.0, 6000, 12000], result.Grids.Where(grid => grid.Vertical).Select(grid => grid.X0));
        Assert.Equal([0.0, 8000], result.Grids.Where(grid => !grid.Vertical).Select(grid => grid.Y0));
        Assert.Contains(result.Styles[0].Reasons, reason => reason.Contains("CENTER"));
    }

    [Fact]
    public void Real_names_from_bubbles_are_kept_even_when_they_break_the_usual_order()
    {
        var drawing = Drawing.Plan("KC-01", "Center 2x", ["1", "1'", "2"], ["A", "C"]);

        var result = GridRecognizer.Recognize(drawing.Case);

        Assert.Equal(["1", "1'", "2", "A", "C"], result.Grids.Select(grid => grid.Name));
    }

    [Fact]
    public void Without_a_grid_pattern_or_bubbles_the_layer_name_decides_and_names_are_numbered()
    {
        var drawing = Drawing.Plan("Trục", "Solid", null, null);

        var result = GridRecognizer.Recognize(drawing.Case);

        Assert.Equal([0], result.ChosenStyles);
        Assert.Equal(["1", "2", "3", "A", "B"], result.Grids.Select(grid => grid.Name));
        Assert.All(result.Grids, grid => Assert.Equal("auto", grid.NameSource));
    }

    [Fact]
    public void Missing_bubble_names_are_numbered_around_the_names_that_were_read()
    {
        var drawing = Drawing.Plan("KC-01", "DASHDOT", ["1", null, "3"], ["A", "B"]);

        var result = GridRecognizer.Recognize(drawing.Case);

        var middle = result.Grids.Single(grid => grid.Vertical && grid.X0 == 6000);
        Assert.Equal("auto", middle.NameSource);
        Assert.DoesNotContain(middle.Name, new[] { "1", "3" });
        Assert.NotEmpty(middle.Reasons);
    }

    [Fact]
    public void A_line_without_a_bubble_among_named_grids_is_an_extra_line_named_after_its_neighbour()
    {
        var drawing = Drawing.Plan("KC-01", "CENTER", ["1", "2", "3"], ["A", "B"]);
        // A slab edge on the grid layer, 1800 below A, with no bubble.
        drawing.Line(drawing.Case.Lines[0].Style, -1000, -1800, 13000, -1800);

        var result = GridRecognizer.Recognize(drawing.Case);

        var horizontal = result.Grids.Where(grid => !grid.Vertical).ToList();
        Assert.Equal(["A'", "A", "B"], horizontal.Select(grid => grid.Name));
        Assert.Equal("extra", horizontal[0].NameSource);
        Assert.Equal(["bubble", "bubble"], horizontal.Skip(1).Select(grid => grid.NameSource));
        Assert.Contains(horizontal[0].Reasons, reason => reason.Contains("đường phụ"));
    }

    [Fact]
    public void A_bubble_drawn_well_past_the_end_of_its_axis_with_a_leader_still_names_it()
    {
        var drawing = Drawing.Plan("KC-01", "CENTER", ["1", "2", "3"], null);
        // Horizontal axes end at x = -1000; their bubbles sit 3.5 m further out, as on a real plan.
        drawing.Bubble(0, -4500, 0, "A");
        drawing.Bubble(0, -4500, 8000, "B");
        // A round column on axis B, inside the drawing, is not a bubble.
        drawing.Case.Arcs.Add(new CadArc { Style = 0, Cx = 1500, Cy = 8000, R = 400, IsCircle = true });

        var result = GridRecognizer.Recognize(drawing.Case);

        Assert.Equal(["A", "B"], result.Grids.Where(grid => !grid.Vertical).Select(grid => grid.Name));
        Assert.All(result.Grids, grid => Assert.Equal("bubble", grid.NameSource));
    }

    [Fact]
    public void Nothing_is_returned_when_no_style_looks_like_grids()
    {
        var drawing = Drawing.Plan("KC-01", "Solid", null, null);

        var result = GridRecognizer.Recognize(drawing.Case);

        Assert.Empty(result.Grids);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void A_named_layer_is_used_even_without_any_grid_evidence()
    {
        var drawing = Drawing.Plan("KC-01", "Solid", null, null);

        Assert.Empty(GridRecognizer.Recognize(drawing.Case).Grids);
        var result = GridRecognizer.Recognize(drawing.Case, layer: "kc-01");

        Assert.Equal(["1", "2", "3", "A", "B"], result.Grids.Select(grid => grid.Name));
    }

    [Fact]
    public void A_named_layer_without_long_lines_gives_nothing_and_says_why()
    {
        var result = GridRecognizer.Recognize(Drawing.Plan("KC-01", "CENTER", null, null).Case, layer: "TEXT");

        Assert.Empty(result.Grids);
        Assert.Contains(result.Warnings, warning => warning.Contains("\"TEXT\""));
    }

    [Fact]
    public void Skewed_grid_lines_are_reported_not_dropped_silently()
    {
        var drawing = Drawing.Plan("KC-01", "CENTER", ["1", "2", "3"], ["A", "B"]);
        drawing.Line(0, 12000, 0, 20000, 6000);

        var result = GridRecognizer.Recognize(drawing.Case);

        Assert.Equal(5, result.Grids.Count);
        Assert.Contains(result.Warnings, warning => warning.Contains("1 đường dài xiên"));
    }

    [Fact]
    public void Comparison_with_the_truth_reports_matches_wrong_names_and_misses()
    {
        var drawing = Drawing.Plan("KC-01", "CENTER", ["1", "2", "3"], ["A", "B"]);
        var recognized = GridRecognizer.Recognize(drawing.Case).Grids;
        TruthGrid[] truth =
        [
            new() { Name = "1", X0 = 0, Y0 = 0, X1 = 0, Y1 = 9000 },
            new() { Name = "2", X0 = 6020, Y0 = 0, X1 = 6020, Y1 = 9000 },
            new() { Name = "4", X0 = 12000, Y0 = 0, X1 = 12000, Y1 = 9000 },
            new() { Name = "A", X0 = 0, Y0 = 0, X1 = 12000, Y1 = 0 },
            new() { Name = "D", X0 = 0, Y0 = 20000, X1 = 12000, Y1 = 20000 },
        ];

        var comparison = GridComparison.Compare(recognized, truth);

        Assert.Equal(4, comparison.Matched);
        Assert.Equal(["4 (đọc ra \"3\")"], comparison.WrongNames);
        Assert.Equal(["D"], comparison.Missing);
        Assert.Equal(["B"], comparison.Extra);
        Assert.False(comparison.IsPerfect);
    }
}
