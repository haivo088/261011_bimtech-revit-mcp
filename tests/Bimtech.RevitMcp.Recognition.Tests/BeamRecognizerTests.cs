using Bimtech.RevitMcp.Recognition;
using Xunit;
using static Bimtech.RevitMcp.Recognition.BeamRecognizer;

namespace Bimtech.RevitMcp.Recognition.Tests;

public sealed class BeamRecognizerTests
{
    // Columns 300x300 at u = 0, 6000, 12000 and v = 0, 6000, named like grid crossings ("A-1" at u=0, v=0).
    private static Support Column(double u, double v) =>
        new($"{(char)('A' + (int)(v / 6000))}-{1 + (int)(u / 6000)}", u - 150, u + 150, v - 150, v + 150, u, v, true);

    private static List<Support> Columns(params (double U, double V)[] at) => at.Select(p => Column(p.U, p.V)).ToList();

    private static readonly (double, double)[] Six = [(0, 0), (6000, 0), (12000, 0), (0, 6000), (6000, 6000), (12000, 6000)];

    // The two edges of a beam 220 wide along u at v, drawn from u0 to u1.
    private static IEnumerable<Line> BeamU(double v, double u0, double u1, double width = 220) =>
        [new(u0, v - width / 2, u1, v - width / 2), new(u0, v + width / 2, u1, v + width / 2)];

    private static IEnumerable<Line> BeamV(double u, double v0, double v1, double width = 220) =>
        [new(u - width / 2, v0, u - width / 2, v1), new(u + width / 2, v0, u + width / 2, v1)];

    private static List<Line> Frame() =>
    [
        // Along u: edges drawn through the middle column on A, span by span on B.
        .. BeamU(0, 150, 11850),
        .. BeamU(6000, 150, 5850), .. BeamU(6000, 6150, 11850),
        .. BeamV(0, 150, 5850), .. BeamV(6000, 150, 5850), .. BeamV(12000, 150, 5850),
    ];

    private static string Span(Beam beam) => $"{beam.From.Label}>{beam.To.Label}";

    private static List<string> Spans(Result result) =>
        result.Beams.Select(b => string.Join(">", new[] { b.From.Label, b.To.Label }.Order())).Order().ToList();

    [Fact]
    public void Beams_run_centre_to_centre_between_neighbouring_columns()
    {
        var result = Recognize(Frame(), Columns(Six), []);

        Assert.Equal(["A-1>A-2", "A-1>B-1", "A-2>A-3", "A-2>B-2", "A-3>B-3", "B-1>B-2", "B-2>B-3"], Spans(result));
        var a12 = result.Beams.Single(b => Span(b) is "A-1>A-2" or "A-2>A-1");
        Assert.True(a12.AlongU);
        Assert.Equal(0, a12.Cross, 6);
        Assert.Equal(220, a12.WidthMm, 6);
        Assert.Equal([0.0, 6000], new[] { a12.Start, a12.End }.Order());
    }

    [Fact]
    public void Edges_broken_where_another_beam_crosses_still_make_one_beam()
    {
        // Beam on B broken at u = 2890..3110 by a secondary beam crossing it.
        List<Line> lines = [.. BeamU(6000, 150, 2890), .. BeamU(6000, 3110, 5850)];

        var result = Recognize(lines, Columns((0, 6000), (6000, 6000)), []);

        Assert.Equal(["B-1>B-2"], Spans(result));
    }

    [Fact]
    public void A_grid_crossing_without_a_column_does_not_split_the_beam()
    {
        var result = Recognize([.. BeamU(6000, 150, 11850)], Columns((0, 6000), (12000, 6000)), []);

        var beam = Assert.Single(result.Beams);
        Assert.Equal([0.0, 12000], new[] { beam.Start, beam.End }.Order());
    }

    [Fact]
    public void A_centre_line_between_the_edges_is_not_taken_as_an_edge()
    {
        List<Line> lines = [.. BeamU(0, 150, 5850), new(150, 0, 5850, 0)];

        var beam = Assert.Single(Recognize(lines, Columns((0, 0), (6000, 0)), []).Beams);

        Assert.Equal(220, beam.WidthMm, 6);
    }

    [Fact]
    public void Column_outlines_are_dropped_and_skewed_lines_counted()
    {
        List<Line> lines =
        [
            .. BeamU(0, 150, 5850),
            new(-150, -150, 150, -150), new(150, -150, 150, 150), new(150, 150, -150, 150), new(-150, 150, -150, -150),
            new(1000, 1000, 2000, 1500),
        ];

        var result = Recognize(lines, Columns((0, 0), (6000, 0)), []);

        Assert.Single(result.Beams);
        Assert.Equal(4, result.MaskedLines);
        Assert.Equal(1, result.SkewedLines);
    }

    [Fact]
    public void Edges_that_reach_no_support_are_dangling()
    {
        var result = Recognize([.. BeamU(0, 150, 2000)], Columns((0, 0), (6000, 0)), []);

        Assert.Empty(result.Beams);
        var dangling = Assert.Single(result.Dangling);
        Assert.Equal("A-1", dangling.From.Label);
        Assert.Equal(1850, dangling.LengthMm, 6);
    }

    [Fact]
    public void A_wall_ends_a_beam_at_its_centre_line()
    {
        var wall = new Support("V1", 8900, 9100, -2000, 2000, 9000, 0, false);

        var beam = Assert.Single(Recognize([.. BeamU(0, 150, 8900)], [Column(0, 0), wall], []).Beams);

        Assert.Equal([0.0, 9000], new[] { beam.Start, beam.End }.Order());
    }

    [Fact]
    public void Lines_between_columns_with_too_little_drawn_are_not_a_beam()
    {
        // Only 1000 of 5700 mm drawn, then a gap far beyond maxGapMm.
        List<Line> lines = [.. BeamU(0, 150, 1150), .. BeamU(0, 4850, 5850)];

        Assert.Empty(Recognize(lines, Columns((0, 0), (6000, 0)), []).Beams);
    }

    [Fact]
    public void Marks_go_to_the_parallel_beam_and_spread_along_its_line()
    {
        List<Text> texts =
        [
            new("D1 (220x600)", 3000, 300, 0),
            new("D2(220x500)", 300, 3000, Math.PI / 2),
            new("ghi chú", 3000, 3000, 0),
        ];

        var result = Recognize(Frame(), Columns(Six), texts);

        var onA = result.Beams.Where(b => b.AlongU && b.Cross == 0).ToList();
        Assert.Equal(2, onA.Count);
        Assert.All(onA, beam => Assert.Equal("D1", beam.Tag?.Name));
        Assert.Single(onA, beam => beam.TagInherited);
        var onGrid1 = result.Beams.Single(b => !b.AlongU && b.Cross == 0);
        Assert.Equal("D2", onGrid1.Tag?.Name);
        Assert.Equal(500, onGrid1.Tag?.HMm);
        Assert.Empty(result.UnusedTags);
    }

    [Fact]
    public void Beam_marks_are_read_in_their_usual_forms()
    {
        Assert.Equal(new BeamTag("D2-X7(220x600)", "D2-X7", 220, 600), BeamTags.Parse("D2-X7(220x600)"));
        Assert.Equal((null, 300.0, 700.0), Fields(BeamTags.Parse("300X700")));
        Assert.Equal(("DC1", 250.0, 500.0), Fields(BeamTags.Parse("{\\fArial|b0;DC1}\\P250*500")));
        Assert.Equal(("DP3", null, null), Fields(BeamTags.Parse("DP3")));
        Assert.Null(BeamTags.Parse("Ghi chú chung"));
        Assert.Null(BeamTags.Parse("+3.600"));
    }

    private static (string?, double?, double?) Fields(BeamTag? tag) => (tag?.Name, tag?.BMm, tag?.HMm);
}
