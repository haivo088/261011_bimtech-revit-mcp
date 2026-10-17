using Bimtech.RevitMcp.Recognition;
using Xunit;

namespace Bimtech.RevitMcp.Recognition.Tests;

// Only groups that pass every check reach 0.9, where the review window ticks them; the rest stay below.
public sealed class ConfidenceRulesTests
{
    private const double AutoApproveFrom = 0.9;

    [Fact]
    public void Columns_are_sure_only_at_crossings_with_an_existing_type()
    {
        Assert.Equal(ConfidenceRules.Sure, ConfidenceRules.Columns(18, 18, existingType: true, tagMismatch: false));
        Assert.True(ConfidenceRules.Columns(18, 17, existingType: true, tagMismatch: false) < AutoApproveFrom);
        Assert.True(ConfidenceRules.Columns(18, 18, existingType: false, tagMismatch: false) < AutoApproveFrom);
        Assert.True(ConfidenceRules.Columns(18, 18, existingType: true, tagMismatch: true) < AutoApproveFrom);
        Assert.Equal(0, ConfidenceRules.Columns(4, 0, existingType: true, tagMismatch: false));
    }

    [Fact]
    public void Walls_are_sure_with_hatch_concrete_thickness_and_three_reasons_each()
    {
        string[] strong = ["+1 có hatch", "+1 dày đúng cỡ vách bê tông", "+1 thuộc cụm vách (lõi)"];
        string[] noConcrete = ["+1 có hatch", "+1 nối vào cột", "+1 thuộc cụm vách (lõi)"];
        string[] twoReasons = ["+1 có hatch", "+1 dày đúng cỡ vách bê tông", "-1 ngắn so với bề dày, có thể là cột dẹt"];

        Assert.Equal(ConfidenceRules.Sure, ConfidenceRules.Walls(true, [strong, strong], repaired: false));
        Assert.True(ConfidenceRules.Walls(true, [strong, noConcrete], repaired: false) < AutoApproveFrom);
        Assert.True(ConfidenceRules.Walls(true, [twoReasons], repaired: false) < AutoApproveFrom);
        Assert.True(ConfidenceRules.Walls(true, [strong], repaired: true) < AutoApproveFrom);
        Assert.True(ConfidenceRules.Walls(false, [strong], repaired: false) < AutoApproveFrom);
        Assert.Null(ConfidenceRules.Walls(null, [strong], repaired: false));
    }

    [Fact]
    public void Beams_are_sure_only_when_the_mark_gives_the_size_and_every_beam_is_clean()
    {
        Assert.Equal(ConfidenceRules.Sure, ConfidenceRules.Beams(12, sizeFromMark: true, 0, 0, 0));
        Assert.True(ConfidenceRules.Beams(12, sizeFromMark: false, 0, 0, 0) < AutoApproveFrom);
        Assert.True(ConfidenceRules.Beams(12, sizeFromMark: true, inheritedMarks: 1, 0, 0) < AutoApproveFrom);
        Assert.True(ConfidenceRules.Beams(12, sizeFromMark: true, 0, widthMismatches: 1, 0) < AutoApproveFrom);
        Assert.True(ConfidenceRules.Beams(12, sizeFromMark: true, 0, 0, brokenLines: 1) < AutoApproveFrom);
    }
}
