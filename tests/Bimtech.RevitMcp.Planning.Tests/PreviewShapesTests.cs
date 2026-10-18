using System.Text.Json;
using Bimtech.RevitMcp.Planning;
using Bimtech.RevitMcp.Ui;
using Xunit;

namespace Bimtech.RevitMcp.Planning.Tests;

// The drawing of items that do not exist yet, read from the same params the create_* actions write from.
public sealed class PreviewShapesTests
{
    [Fact]
    public void A_pile_cap_draws_its_outline_and_a_circle_per_pile()
    {
        var shapes = PreviewShapes.From("create_pile_cap", Params(new
        {
            capLevelId = "311",
            outline = new[] { new[] { 0.0, 0 }, new[] { 2000.0, 0 }, new[] { 2000.0, 1000 }, new[] { 0.0, 1000 } },
            piles = new[] { new { x = 500.0, y = 500.0, diameterMm = 600.0 }, new { x = 1500.0, y = 500.0, diameterMm = 600.0 } },
        }));

        Assert.Equal(3, shapes.Count);
        Assert.True(shapes[0].Closed);
        Assert.Equal([new(0, 0), new(2000, 0), new(2000, 1000), new(0, 1000)], shapes[0].Points);
        var pile = shapes[1];
        Assert.True(pile.Closed);
        Assert.Equal(PreviewShapes.CircleSegments, pile.Points.Count);
        Assert.All(pile.Points, point => Assert.Equal(300, Distance(point, new PointMm(500, 500)), 6));
        Assert.Equal(1500, shapes[2].Points.Average(point => point.X), 6);
    }

    [Fact]
    public void A_column_is_a_rectangle_with_its_length_along_the_angle()
    {
        var shapes = PreviewShapes.From("create_columns", Params(new
        {
            columns = new[]
            {
                new { x = 1000.0, y = 2000.0, angleDeg = 0.0, lengthMm = 500.0, widthMm = 300.0 },
                new { x = 0.0, y = 0.0, angleDeg = 90.0, lengthMm = 500.0, widthMm = 300.0 },
            },
        }));

        Assert.Equal(2, shapes.Count);
        Assert.True(shapes[0].Closed);
        var (min, max) = PreviewShapes.Extent([shapes[0]])!.Value;
        Assert.Equal(new PointMm(750, 1850), min);
        Assert.Equal(new PointMm(1250, 2150), max);
        // Turned a quarter: the 500 side now runs along Y.
        var (turnedMin, turnedMax) = PreviewShapes.Extent([shapes[1]])!.Value;
        Assert.Equal(300, turnedMax.X - turnedMin.X, 6);
        Assert.Equal(500, turnedMax.Y - turnedMin.Y, 6);
    }

    [Fact]
    public void Beams_walls_and_grids_draw_their_axes()
    {
        var beams = PreviewShapes.From("create_beams", Params(new
        {
            levelId = "7",
            beams = new[] { new { x0 = 0.0, y0 = 0.0, x1 = 6000.0, y1 = 0.0 }, new { x0 = 0.0, y0 = 0.0, x1 = 0.0, y1 = 4000.0 } },
        }));
        var walls = PreviewShapes.From("create_walls", Params(new { lines = new[] { new[] { 0.0, 0, 0, 3000 } } }));
        var grid = PreviewShapes.From("create_grid", Params(new
        {
            name = "B", startMm = new { x = -1000.0, y = 6000.0 }, endMm = new { x = 25000.0, y = 6000.0 },
        }));

        Assert.Equal(2, beams.Count);
        Assert.All(beams, beam => Assert.False(beam.Closed));
        Assert.Equal([new(0, 0), new(6000, 0)], beams[0].Points);
        Assert.Equal([new(0, 0), new(0, 3000)], Assert.Single(walls).Points);
        Assert.Equal([new(-1000, 6000), new(25000, 6000)], Assert.Single(grid).Points);
    }

    [Fact]
    public void A_floor_draws_its_closed_outline()
    {
        var floor = Assert.Single(PreviewShapes.From("create_floor", Params(new
        {
            levelId = "7", points = new[] { new[] { 0.0, 0 }, new[] { 4000.0, 0 }, new[] { 4000.0, 3000 } },
        })));

        Assert.True(floor.Closed);
        Assert.Equal(3, floor.Points.Count);
    }

    [Fact]
    public void Malformed_parts_are_left_out_and_other_actions_draw_nothing()
    {
        var shapes = PreviewShapes.From("create_beams", Params(new
        {
            beams = new object[] { new { x0 = 0.0, y0 = 0.0, x1 = 100.0 }, new { x0 = 0.0, y0 = 0.0, x1 = 100.0, y1 = 0.0 } },
        }));
        var walls = PreviewShapes.From("create_walls", Params(new { lines = new object[] { new[] { 0.0, 0, 1 }, "x" } }));
        var cap = PreviewShapes.From("create_pile_cap", Params(new { outline = new[] { new[] { 0.0, 0 }, new[] { 1.0, 0 } } }));

        Assert.Single(shapes);
        Assert.Empty(walls);
        Assert.Empty(cap);
        Assert.Empty(PreviewShapes.From("set_parameter", Params(new { elementId = "1", parameter = "Comments", value = "x" })));
        Assert.Empty(PreviewShapes.From("create_columns", default));
        Assert.Null(PreviewShapes.Extent([]));
    }

    [Fact]
    public void The_level_comes_from_the_first_level_field_the_action_uses()
    {
        Assert.Equal("311", PreviewShapes.LevelId(Params(new { capLevelId = "311", levelId = "9" })));
        Assert.Equal("5", PreviewShapes.LevelId(Params(new { baseLevelId = "5", topLevelId = "6" })));
        Assert.Null(PreviewShapes.LevelId(Params(new { name = "B" })));
    }

    [Fact]
    public void Rows_carry_the_preview_of_new_items_only()
    {
        var create = new PlanItemRow(new PlanItem
        {
            ItemId = "C1",
            Action = "create_columns",
            Params = Params(new { baseLevelId = "5", columns = new[] { new { x = 0.0, y = 0.0, angleDeg = 0.0, lengthMm = 400.0, widthMm = 400.0 } } }),
        });
        var change = new PlanItemRow(new PlanItem
        {
            ItemId = "E1",
            Action = "set_parameter",
            Params = Params(new { elementId = "1", parameter = "Comments", value = "x" }),
        });

        Assert.Equal("5", create.Preview.LevelId);
        Assert.Single(create.Preview.Shapes);
        Assert.Same(PreviewGroup.None, change.Preview);
    }

    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    private static double Distance(PointMm a, PointMm b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
