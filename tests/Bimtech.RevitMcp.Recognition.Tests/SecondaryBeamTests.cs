using Bimtech.RevitMcp.Recognition;
using Xunit;
using Line = Bimtech.RevitMcp.Recognition.BeamRecognizer.Line;

namespace Bimtech.RevitMcp.Recognition.Tests;

// Secondary beams: the next round after main beams, resting on the beams already in the model.
public sealed class SecondaryBeamTests
{
    // A 6 m bay: columns 400 x 400 at its corners, main beams 300 wide along its four sides (in the model).
    private static List<BeamRecognizer.Support> Bay()
    {
        var supports = new List<CaseSupport>();
        foreach (var (x, y) in new[] { (0.0, 0.0), (6000.0, 0.0), (0.0, 6000.0), (6000.0, 6000.0) })
            supports.Add(new CaseSupport { Cx = x, Cy = y, Points = [[x - 200, y - 200], [x + 200, y - 200], [x + 200, y + 200], [x - 200, y + 200]] });
        foreach (var (x0, y0, x1, y1, mark) in new[]
        {
            (0.0, 0.0, 6000.0, 0.0, "D1"), (0.0, 6000.0, 6000.0, 6000.0, "D2"), (0.0, 0.0, 0.0, 6000.0, "D3"), (6000.0, 0.0, 6000.0, 6000.0, "D4"),
        })
            supports.Add(BeamCase.BeamSupport(new TruthBeam { Mark = mark, X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, BMm = 300, HMm = 600 }));
        return BeamCase.Supports(supports, BeamCase.Frame.Plain);
    }

    // The two edges of a beam along v at u = x, b wide, from one main beam's face to the other's.
    private static List<Line> Across(double x, double b, bool dashed = false) =>
    [
        new(x - b / 2, 150, x - b / 2, 5850, dashed),
        new(x + b / 2, 150, x + b / 2, 5850, dashed),
    ];

    [Fact]
    public void A_beam_between_two_main_beams_rests_on_their_centre_lines()
    {
        var result = BeamRecognizer.Recognize(Across(3000, 220), Bay(), [], new BeamRecognizer.Options { Secondary = true });

        var beam = Assert.Single(result.Beams, b => b.IsSecondary);
        Assert.False(beam.AlongU);
        Assert.Equal(3000, beam.Cross, 6);
        Assert.Equal((0.0, 6000.0), (Math.Min(beam.Start, beam.End), Math.Max(beam.Start, beam.End)));
        Assert.Equal(220, beam.WidthMm, 6);
        Assert.Equal("dầm D1", beam.Start < beam.End ? beam.From.Label : beam.To.Label);
    }

    [Fact]
    public void Main_beams_only_leave_columns()
    {
        var result = BeamRecognizer.Recognize(Across(3000, 220), Bay(), []);

        Assert.DoesNotContain(result.Beams, b => b.IsSecondary);
    }

    [Fact]
    public void A_beam_at_mid_span_drawn_dashed_with_a_mark_is_sure()
    {
        var texts = new List<BeamRecognizer.Text> { new("DP1 (220x400)", 3300, 3000, Math.PI / 2) };
        var result = BeamRecognizer.Recognize(Across(3000, 220, dashed: true), Bay(), texts, new BeamRecognizer.Options { Secondary = true });
        var beam = result.Beams.Single(b => b.IsSecondary);

        var weight = SecondaryBeams.Weigh(beam, result.Beams, BeamCase.Frame.Plain, []);

        Assert.True(weight.Sure);
        Assert.Equal("DP1", beam.Tag?.Name);
        Assert.Contains("+1 " + SecondaryBeams.DivisionReason, weight.Reasons);
        Assert.Contains("+1 " + SecondaryBeams.DashedReason, weight.Reasons);
        Assert.Contains("+1 " + SecondaryBeams.MarkReason, weight.Reasons);
    }

    [Fact]
    public void A_beam_wider_than_its_support_or_on_a_wall_is_not_sure()
    {
        var result = BeamRecognizer.Recognize(Across(2000, 400), Bay(), [], new BeamRecognizer.Options { Secondary = true });
        var beam = result.Beams.Single(b => b.IsSecondary);
        // A 220 masonry wall hatched under it.
        List<(double U, double V)> wall = [(1890, 200), (2110, 200), (2110, 5800), (1890, 5800)];

        var weight = SecondaryBeams.Weigh(beam, result.Beams, BeamCase.Frame.Plain, [wall]);

        Assert.False(weight.Sure);
        Assert.Contains("-3 " + SecondaryBeams.BiggerReason, weight.Reasons);
        Assert.Contains("-2 " + SecondaryBeams.WallReason, weight.Reasons);
    }
}
