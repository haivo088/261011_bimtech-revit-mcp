using Bimtech.RevitMcp.Recognition;
using Xunit;
using static Bimtech.RevitMcp.Recognition.Shapes;

namespace Bimtech.RevitMcp.Recognition.Tests;

public sealed class ColumnShapeTests
{
    // A 300x500 column centred on the origin, 500 along X.
    private static readonly Rectangle Column = new(0, 0, 500, 300, 0);

    private static List<Segment> Box(double halfX, double halfY, double dx = 0) =>
    [
        new(-halfX + dx, -halfY, halfX + dx, -halfY), new(halfX + dx, -halfY, halfX + dx, halfY),
        new(halfX + dx, halfY, -halfX + dx, halfY), new(-halfX + dx, halfY, -halfX + dx, -halfY),
    ];

    [Fact]
    public void Four_sides_make_a_rectangle_even_with_hatching_inside()
    {
        var segments = Box(250, 150);
        segments.Add(new Segment(-250, -150, 50, 150)); // hatch line ending on the outline

        Assert.True(IsRectangle(segments, Column));
    }

    [Fact]
    public void A_rectangle_drawn_with_the_last_side_missing_by_a_little_still_counts()
    {
        var segments = Box(250, 150);
        segments[3] = new Segment(-250, 150, -250, -120); // 270 of 300 drawn

        Assert.True(IsRectangle(segments, Column));
    }

    [Fact]
    public void An_arrow_or_an_open_corner_is_not_a_rectangle()
    {
        Assert.False(IsRectangle([new(-250, -150, 250, -150), new(250, -150, 250, 150)], Column));
    }

    [Fact]
    public void An_L_shaped_column_is_not_its_bounding_rectangle()
    {
        // L: 500x300 box with a 250x150 notch cut from one corner.
        List<Segment> l =
        [
            new(-250, -150, 250, -150), new(250, -150, 250, 0), new(250, 0, 0, 0),
            new(0, 0, 0, 150), new(0, 150, -250, 150), new(-250, 150, -250, -150),
        ];

        Assert.False(IsRectangle(l, Column));
    }

    [Fact]
    public void A_column_merged_with_a_touching_wall_is_not_a_rectangle()
    {
        // Column 300x300 at the origin with a 200-thick wall running from its right side to x = 2150.
        var segments = Box(150, 150);
        segments.Add(new Segment(150, -100, 2150, -100));
        segments.Add(new Segment(150, 100, 2150, 100));

        Assert.False(IsRectangle(segments, new Rectangle(1000, 0, 2300, 300, 0)));
    }

    [Fact]
    public void Touching_lines_are_clustered_and_far_ones_are_not()
    {
        var segments = Box(150, 150).Concat(Box(150, 150, dx: 5000)).ToList();
        segments.Add(new Segment(9000, 9000, 9100, 9000));

        var clusters = Clusters(segments, 2);

        Assert.Equal(3, clusters.Count);
        Assert.Equal([4, 4, 1], clusters.Select(cluster => cluster.Count).OrderByDescending(count => count));
    }

    [Fact]
    public void Clustering_tens_of_thousands_of_lines_is_fast()
    {
        var segments = Enumerable.Range(0, 40_000).Select(i => new Segment(i * 10, 0, i * 10 + 5, 0)).ToList();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var clusters = Clusters(segments, 2);

        Assert.Equal(40_000, clusters.Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
    }

    [Theory]
    [InlineData("C1 300x500", "C1", 300.0, 500.0)]
    [InlineData("C12A(500X300)", "C12A", 300.0, 500.0)]
    [InlineData("CỘT 4 - 220x220", "CỘT4", 220.0, 220.0)]
    [InlineData("CT3", "CT3", null, null)]
    public void Column_marks_give_name_and_size(string text, string name, double? a, double? b)
    {
        var tag = ColumnTags.Parse(text);

        Assert.Equal(name, tag?.Name);
        Assert.Equal(a, tag?.SizeAMm);
        Assert.Equal(b, tag?.SizeBMm);
    }

    [Theory]
    [InlineData("D2-X7(220x600)")]
    [InlineData("Đ1 (2000x2000x1200)")]
    [InlineData("MB TẦNG 2")]
    public void Beam_cap_and_title_texts_are_not_column_marks(string text)
    {
        Assert.Null(ColumnTags.Parse(text));
    }

    [Fact]
    public void Each_mark_labels_only_its_nearest_column()
    {
        (double, double)[] columns = [(0, 0), (1000, 0), (9000, 9000)];
        (string, double, double)[] texts = [("C1", 300, 400), ("C2", 1200, 400), ("C9", 50000, 0)];

        var tags = ColumnTags.AssignNearest(columns, texts, ColumnTags.Parse, ColumnTags.MaxDistanceMm);

        Assert.Equal(["C1", "C2", null], tags.Select(tag => tag?.Name));
    }
}
