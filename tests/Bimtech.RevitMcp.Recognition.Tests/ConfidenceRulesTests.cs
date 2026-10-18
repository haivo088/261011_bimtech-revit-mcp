using Bimtech.RevitMcp.Recognition;
using Xunit;

namespace Bimtech.RevitMcp.Recognition.Tests;

// One scale: 50% means no evidence either way, which is also where items start approved.
public sealed class ConfidenceRulesTests
{
    private const double AutoApproveFrom = Evidence.AutoFromPercent / 100.0;

    [Fact]
    public void An_item_is_as_sure_as_its_least_sure_element()
    {
        Assert.Equal(0.6, ConfidenceRules.Group([90, 60, 100]));
        Assert.Equal(0.5, ConfidenceRules.Group([]));
        Assert.True(ConfidenceRules.Group([90, 45]) < AutoApproveFrom);
    }

    [Fact]
    public void A_type_to_create_always_needs_a_look()
    {
        Assert.True(ConfidenceRules.Group([100, 95], needsLook: true) < AutoApproveFrom);
        Assert.Equal(0.3, ConfidenceRules.Group([30], needsLook: true));
    }

    [Fact]
    public void Main_beams_are_sure_unless_several_things_are_off()
    {
        Assert.Equal(80, ConfidenceRules.MainBeam(sizeFromMark: true, inheritedMark: false, widthMismatch: false, broken: false, onGrid: true));
        Assert.Equal(70, ConfidenceRules.MainBeam(sizeFromMark: false, inheritedMark: false, widthMismatch: false, broken: false, onGrid: true));
        Assert.True(Evidence.IsSure(ConfidenceRules.MainBeam(true, inheritedMark: true, widthMismatch: true, broken: false, onGrid: true)));
        Assert.False(Evidence.IsSure(ConfidenceRules.MainBeam(true, inheritedMark: true, widthMismatch: true, broken: true, onGrid: false)));
    }

    [Fact]
    public void Evidence_reasons_carry_their_weight_in_percent()
    {
        Assert.Equal("+20% ở giao trục", Evidence.Reason(20, "ở giao trục"));
        Assert.Equal("-50% đoạn vách", Evidence.Reason(-50, "đoạn vách"));
        Assert.True(Evidence.IsKept(20));
        Assert.False(Evidence.IsKept(19));
    }
}
