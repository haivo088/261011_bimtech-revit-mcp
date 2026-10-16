using Bimtech.RevitMcp.Recognition;
using Xunit;
using static Bimtech.RevitMcp.Recognition.GridIntersections;

namespace Bimtech.RevitMcp.Recognition.Tests;

public sealed class GridIntersectionsTests
{
    // Grids 1, 2, 3 at u = 0, 6000, 12000, running along v from -1000 to 13000.
    private static readonly Axis[] Numbers =
        [new("1", 0, -1000, 13000), new("2", 6000, -1000, 13000), new("3", 12000, -1000, 13000)];
    // Grids A, B, C at v = 0, 6000, 12000, running along u.
    private static readonly Axis[] Letters =
        [new("A", 0, -1000, 13000), new("B", 6000, -1000, 13000), new("C", 12000, -1000, 13000)];

    [Fact]
    public void A_column_drawn_a_few_mm_off_the_crossing_is_set_on_it()
    {
        var fit = Fit(6007, 150, Numbers, 5);

        Assert.Equal(Placement.Centered, fit.Placement);
        Assert.Equal("2", fit.GridName);
        Assert.Equal(0, fit.OffsetMm);
        Assert.Equal(-7, fit.ShiftMm, 6);
    }

    [Fact]
    public void A_column_with_its_face_near_the_grid_gets_the_face_exactly_on_it()
    {
        // 300 wide, drawn with its centre 142 mm left of grid 3: the face is meant to be on the grid.
        var fit = Fit(12000 - 142, 150, Numbers, 5);

        Assert.Equal(Placement.FaceOnGrid, fit.Placement);
        Assert.Equal(-150, fit.OffsetMm);
    }

    [Fact]
    public void Any_other_offset_on_a_grid_is_rounded_to_the_snap()
    {
        var fit = Fit(6000 + 98.7, 250, Numbers, 5);

        Assert.Equal(Placement.Offset, fit.Placement);
        Assert.Equal(100, fit.OffsetMm);
        Assert.True(fit.OnGrid);
    }

    [Fact]
    public void A_column_away_from_every_grid_keeps_a_rounded_offset_and_is_off_grid()
    {
        // Midway between grids 1 and 2, nearer 2: 2998 mm before it, kept as 3000.
        var fit = Fit(3002, 150, Numbers, 5);

        Assert.Equal(Placement.OffGrid, fit.Placement);
        Assert.Equal("2", fit.GridName);
        Assert.Equal(-3000, fit.OffsetMm);
        Assert.False(fit.OnGrid);
    }

    [Fact]
    public void A_snap_of_zero_keeps_the_drawing_but_still_classifies()
    {
        var fit = Fit(6007, 150, Numbers, 0);

        Assert.Equal(Placement.Centered, fit.Placement);
        Assert.Equal(0, fit.ShiftMm);
    }

    [Fact]
    public void Without_grids_nothing_moves()
    {
        var fit = Fit(1234, 150, [], 5);

        Assert.Equal(Placement.NoGrid, fit.Placement);
        Assert.Equal(0, fit.ShiftMm);
    }

    [Fact]
    public void The_extent_of_a_column_follows_its_rotation()
    {
        Assert.Equal(250, HalfExtent(500, 300, 0), 6);
        Assert.Equal(150, HalfExtent(500, 300, Math.PI / 2), 6);
    }

    [Fact]
    public void Labels_put_the_letter_grid_first()
    {
        Assert.Equal("A-1", Label("1", "A"));
        Assert.Equal("A-1", Label("A", "1"));
        Assert.Equal("1-2", Label("1", "2"));
    }

    [Fact]
    public void Crossings_without_a_column_are_listed()
    {
        // Columns at every crossing but B-2 (an atrium).
        var columns = new List<Column>();
        foreach (var u in new[] { 0.0, 6000, 12000 })
        foreach (var v in new[] { 0.0, 6000, 12000 })
            if (u != 6000 || v != 6000) columns.Add(new Column(u, v, 150, 150));

        Assert.Equal(["B-2"], Empty(Letters, Numbers, columns));
    }

    [Fact]
    public void Crossings_outside_the_columns_or_where_a_grid_stops_are_not_listed()
    {
        // Columns only on grid A; grid C stops before grid 3.
        Axis[] letters = [Letters[0], Letters[1], new("C", 12000, -1000, 7000)];
        var columns = new List<Column> { new(0, 0, 150, 150), new(6000, 0, 150, 150), new(12000, 0, 150, 150) };

        Assert.Empty(Empty(letters, Numbers, columns));
    }

    [Fact]
    public void A_column_face_on_the_grid_still_counts_for_the_crossing()
    {
        var columns = new List<Column> { new(150, 150, 150, 150) };

        Assert.Empty(Empty([Letters[0]], [Numbers[0]], columns));
    }
}
