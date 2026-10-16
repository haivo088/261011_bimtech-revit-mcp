using Bimtech.RevitMcp.Recognition;
using Xunit;

namespace Bimtech.RevitMcp.Recognition.Tests;

public sealed class PileCapsTests
{
    [Theory]
    [InlineData("Đ1 (2000x2000x1200)", "Đ1", 2000.0, 2000.0, 1200.0)]
    [InlineData("ĐM2 h=1500", "ĐM2", null, null, 1500.0)]
    [InlineData("ĐC3 - 1500X2400 - H=1000", "ĐC3", 2400.0, 1500.0, 1000.0)]
    [InlineData("M4 (1200x1200)", "M4", 1200.0, 1200.0, null)]
    [InlineData("DM 5 H:900", "DM5", null, null, 900.0)]
    public void Cap_marks_give_name_size_and_thickness(string text, string name, double? length, double? width, double? thickness)
    {
        var tag = PileCaps.ParseTag(text);

        Assert.NotNull(tag);
        Assert.Equal(name, tag.Name);
        Assert.Equal(length, tag.LengthMm);
        Assert.Equal(width, tag.WidthMm);
        Assert.Equal(thickness, tag.ThicknessMm);
    }

    [Theory]
    [InlineData("Cọc D600 L=32m")]
    [InlineData("MB MÓNG")]
    [InlineData("C1")]
    public void Other_texts_are_not_cap_marks(string text)
    {
        Assert.Null(PileCaps.ParseTag(text));
    }

    private static readonly (double X, double Y)[] Square = [(0, 0), (2000, 0), (2000, 2000), (0, 2000)];

    [Fact]
    public void A_mark_inside_the_cap_wins_over_a_closer_one_outside()
    {
        var tag = PileCaps.PickTag([("Đ2 (2000x2000x1500)", 2100, 1000), ("Đ1 (2000x2000x1200)", 300, 300)], Square);

        Assert.Equal("Đ1", tag?.Name);
    }

    [Fact]
    public void A_mark_beside_the_cap_is_used_when_none_is_inside_but_not_one_far_away()
    {
        Assert.Equal("Đ1", PileCaps.PickTag([("Đ1 h=1200", 1000, 2600)], Square)?.Name);
        Assert.Null(PileCaps.PickTag([("Đ1 h=1200", 9000, 9000)], Square));
    }

    [Fact]
    public void Distance_to_the_outline_tells_how_far_a_pile_lies_outside()
    {
        Assert.True(PileCaps.Contains(Square, 1000, 1000));
        Assert.False(PileCaps.Contains(Square, 2200, 1000));
        Assert.Equal(200, PileCaps.DistanceToOutline(Square, 2200, 1000), 6);
        Assert.Equal(500, PileCaps.DistanceToOutline(Square, 1000, -500), 6);
    }

    [Theory]
    [InlineData(1498.7, 5, 1500)]
    [InlineData(1497.4, 5, 1495)]
    [InlineData(612.5, 5, 615)]
    [InlineData(612.5, 0, 612.5)]
    public void Sizes_round_to_the_nearest_step(double value, double step, double expected)
    {
        Assert.Equal(expected, PileCaps.RoundTo(value, step), 6);
    }
}
