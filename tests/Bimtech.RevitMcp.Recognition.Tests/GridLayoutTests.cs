using Bimtech.RevitMcp.Recognition;
using Xunit;

namespace Bimtech.RevitMcp.Recognition.Tests;

public sealed class GridLayoutTests
{
    [Fact]
    public void Spacings_near_the_module_are_snapped_and_added_up_from_the_first_axis()
    {
        // Drawn 5998.7, 6001.2 and 4497.9 apart: meant 6000, 6000, 4500.
        var placed = GridLayout.Snap([1000, 6998.7, 12999.9, 17497.8], 50, 10);

        Assert.Equal([0.0, 6000, 12000, 16500], placed.Select(axis => axis.OffsetMm));
        Assert.Null(placed[0].Note);
        Assert.Contains("5998.7 mm → 6000 mm", placed[1].Note);
    }

    [Fact]
    public void Spacings_far_from_the_module_are_kept_and_flagged()
    {
        var placed = GridLayout.Snap([0, 3725.4], 50, 10);

        Assert.Equal(3725, placed[1].OffsetMm);
        Assert.Contains("không gần bội 50 mm", placed[1].Note);
    }

    [Fact]
    public void A_module_of_zero_only_rounds_to_whole_millimetres()
    {
        var placed = GridLayout.Snap([0, 5998.7], 0, 10);

        Assert.Equal(5999, placed[1].OffsetMm);
        Assert.Null(placed[1].Note);
    }

    [Fact]
    public void A_drawing_of_another_storey_is_matched_wherever_it_was_drawn()
    {
        double[] reference = [0, 6000, 12000, 16500];
        // Same grid, drawn 250 m away with small drawing errors.
        Assert.Equal(0, GridLayout.Match(reference, [250000, 256005, 262000, 266490]));
        // Only the last three axes on this storey.
        Assert.Equal(1, GridLayout.Match(reference, [80000, 86000, 90500]));
    }

    [Fact]
    public void Ambiguous_or_unrelated_drawings_are_not_matched()
    {
        // A regular grid: two equal bays fit at two places.
        Assert.Null(GridLayout.Match([0, 6000, 12000, 18000], [500, 6500]));
        // Different spacings.
        Assert.Null(GridLayout.Match([0, 6000, 12000], [0, 5000, 12000]));
        Assert.Null(GridLayout.Match([0, 6000], [0, 6000, 12000]));
    }
}
