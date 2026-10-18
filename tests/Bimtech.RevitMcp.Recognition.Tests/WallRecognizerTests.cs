using Bimtech.RevitMcp.Recognition;
using Xunit;

namespace Bimtech.RevitMcp.Recognition.Tests;

public class WallRecognizerTests
{
    private sealed class Scene
    {
        public CadCase Case { get; } = new();
        public List<CaseSupport> Columns { get; } = [];
        public List<CaseSupport> Below { get; } = [];
        public List<TruthGrid> Grids { get; } = [];

        public Scene Hatch(string pattern, params double[] xy) => Hatch(pattern, "VACH", xy);

        public Scene Hatch(string pattern, string layer, params double[] xy)
        {
            var style = Case.Styles.FindIndex(s => s.Layer == layer);
            if (style < 0)
            {
                Case.Styles.Add(new CadStyle { Layer = layer });
                style = Case.Styles.Count - 1;
            }
            var loop = Enumerable.Range(0, xy.Length / 2).Select(i => new[] { xy[2 * i], xy[2 * i + 1] }).ToList();
            Case.Hatches.Add(new CadHatch { Style = style, Pattern = pattern, Loops = [loop] });
            return this;
        }

        public Scene Box(string pattern, double x0, double y0, double x1, double y1, string layer = "VACH") =>
            Hatch(pattern, layer, x0, y0, x1, y0, x1, y1, x0, y1);

        public Scene Column(double cx, double cy, double half)
        {
            Columns.Add(new CaseSupport
            {
                Kind = "column", Cx = cx, Cy = cy,
                Points = [[cx - half, cy - half], [cx + half, cy - half], [cx + half, cy + half], [cx - half, cy + half]],
            });
            return this;
        }

        public WallRecognizer.Result Run(WallRecognizer.Region? region = null) =>
            WallRecognizer.Recognize(Case, BeamCase.FrameOf(Grids), Columns, Below, region);
    }

    private static WallRecognizer.Wall Near(WallRecognizer.Result result, double x, double y) =>
        result.Walls.Concat(result.Dropped).OrderBy(w =>
            Math.Min(Math.Abs(w.X0 - x) + Math.Abs(w.Y0 - y), Math.Abs(w.X1 - x) + Math.Abs(w.Y1 - y))).First();

    [Fact]
    public void A_rectangle_hatch_is_one_wall_on_its_centre_line()
    {
        var result = new Scene().Box("FP_1", 0, 0, 300, 4700).Run();

        var wall = Assert.Single(result.Walls);
        Assert.Equal(300, wall.ThicknessMm);
        Assert.Equal((150.0, 150.0), (wall.X0, wall.X1));
        Assert.Equal(4700, wall.Length, 1);
        Assert.Contains(wall.Reasons, r => r.EndsWith(WallRecognizer.ConcreteSizeReason));
    }

    [Fact]
    public void An_L_shaped_hatch_gives_one_wall_per_leg_meeting_at_the_centre_lines()
    {
        // Leg along y: 300 thick from y=0 to 4700; leg along x: 250 thick at the top, to x=2500.
        var result = new Scene().Hatch("FP_1", 0, 0, 300, 0, 300, 4450, 2500, 4450, 2500, 4700, 0, 4700).Run();

        Assert.Equal(2, result.Walls.Count);
        var up = result.Walls.Single(w => w.ThicknessMm == 300);
        var across = result.Walls.Single(w => w.ThicknessMm == 250);
        Assert.Equal(4575, Math.Max(up.Y0, up.Y1), 1);
        Assert.Equal(0, Math.Min(up.Y0, up.Y1), 1);
        Assert.Equal(150, Math.Min(across.X0, across.X1), 1);
        Assert.Equal(2500, Math.Max(across.X0, across.X1), 1);
        Assert.Equal(4575, across.Y0, 1);
    }

    [Fact]
    public void Two_rectangles_drawn_as_an_L_are_joined_like_one()
    {
        var result = new Scene().Box("FP_1", 0, 0, 300, 4700).Box("FP_1", 300, 4450, 2500, 4700).Run();

        var across = result.Walls.Single(w => w.ThicknessMm == 250);
        Assert.Equal(150, Math.Min(across.X0, across.X1), 1);
        var up = result.Walls.Single(w => w.ThicknessMm == 300);
        Assert.Equal(4575, Math.Max(up.Y0, up.Y1), 1);
    }

    [Fact]
    public void Repeated_and_split_hatches_become_one_wall()
    {
        var result = new Scene()
            .Box("FP_1", 0, 0, 300, 2000).Box("FP_1", 0, 0, 300, 2000)
            .Box("FP_1", 0, 2000, 300, 4700)
            .Run();

        var wall = Assert.Single(result.Walls);
        Assert.Equal(4700, wall.Length, 1);
    }

    [Fact]
    public void Masonry_walls_are_dropped_and_say_why()
    {
        var result = new Scene().Box("ANSI31", 0, 0, 5000, 110, "TUONG").Box("ANSI31", 0, 1000, 5000, 1220, "TUONG").Run();

        Assert.Empty(result.Walls);
        Assert.Equal(2, result.Dropped.Count);
        Assert.All(result.Dropped, wall => Assert.Contains(wall.Reasons, r => r.EndsWith(WallRecognizer.MasonrySizeReason)));
    }

    [Fact]
    public void Square_hatches_are_columns_not_walls_and_lend_their_pattern()
    {
        var scene = new Scene()
            .Box("FP_1", -400, -400, 400, 400, "COT").Box("FP_1", 7600, -400, 8400, 400, "COT")
            .Box("FP_1", 400, -150, 7600, 150)
            .Box("SOLID", 0, 3000, 6000, 3300, "0");

        var result = scene.Run();

        Assert.Equal(["FP_1"], result.ColumnPatterns);
        Assert.False(result.ColumnPatternsFromModel);
        var shared = Near(result, 400, 0);
        // Guessed from shape only: a hint.
        Assert.Contains(shared.Reasons, r => r == "+10% " + WallRecognizer.ColumnPatternReason);
        Assert.True(shared.Sure);
        var other = Near(result, 0, 3150);
        Assert.DoesNotContain(other.Reasons, r => r.EndsWith(WallRecognizer.ColumnPatternReason));
        Assert.False(other.Sure);
    }

    [Fact]
    public void Patterns_read_off_modeled_columns_weigh_more()
    {
        var result = new Scene()
            .Box("FP_1", -400, -400, 400, 400, "COT").Box("FP_1", 7600, -400, 8400, 400, "COT")
            .Column(0, 0, 400).Column(8000, 0, 400)
            .Box("FP_1", 400, -150, 7600, 150, "0")
            .Run();

        Assert.True(result.ColumnPatternsFromModel);
        var wall = Assert.Single(result.Walls);
        Assert.Contains(wall.Reasons, r => r == "+20% " + WallRecognizer.ColumnPatternReason);
    }

    [Fact]
    public void Column_sized_hatches_are_never_walls()
    {
        var result = new Scene().Box("FP_1", 0, 0, 300, 800).Box("FP_1", 2000, 0, 2200, 1000).Run();

        Assert.Empty(result.Walls);
        Assert.Empty(result.Dropped);
        Assert.Equal(2, result.Skipped.Values.Sum());
    }

    [Fact]
    public void Grids_columns_and_the_wall_below_add_evidence()
    {
        var scene = new Scene().Box("X", 0, -150, 6000, 150).Column(6200, 0, 200);
        scene.Grids.Add(new TruthGrid { Name = "A", X0 = -2000, Y0 = 0, X1 = 9000, Y1 = 0 });
        scene.Grids.Add(new TruthGrid { Name = "1", X0 = 0, Y0 = -2000, X1 = 0, Y1 = 9000 });
        scene.Below.Add(new CaseSupport
        {
            Kind = "wall",
            Points = [[0, 150], [6000, 150], [6000, -150], [0, -150]],
        });

        var wall = Assert.Single(scene.Run().Walls);

        Assert.Equal("A", wall.Grid);
        Assert.Contains(wall.Reasons, r => r.EndsWith(WallRecognizer.GridReason));
        Assert.Contains(wall.Reasons, r => r.EndsWith(WallRecognizer.ColumnReason));
        Assert.Contains(wall.Reasons, r => r == "+20% " + WallRecognizer.BelowReason);
        Assert.True(wall.Sure);
    }

    [Fact]
    public void A_lift_core_is_recognised_as_a_cluster_with_its_text()
    {
        // A U-shaped core 2400 x 2600 outside, 250 thick, opening at the bottom, lift text inside.
        var scene = new Scene().Hatch("X", 0, 0, 250, 0, 250, 2350, 2150, 2350, 2150, 0, 2400, 0, 2400, 2600, 0, 2600);
        scene.Case.Texts.Add(new CadText { Text = "THANG MÁY", X = 1200, Y = 1200 });

        var result = scene.Run();

        Assert.Equal(3, result.Walls.Count);
        Assert.All(result.Walls, wall =>
        {
            Assert.Contains(wall.Reasons, r => r.EndsWith(WallRecognizer.CoreReason));
            Assert.Contains(wall.Reasons, r => r.EndsWith(WallRecognizer.LiftTextReason));
        });
    }

    [Fact]
    public void Only_hatches_in_the_region_count()
    {
        var result = new Scene().Box("FP_1", 0, 0, 300, 4700).Box("FP_1", 900000, 0, 900300, 4700)
            .Run(new WallRecognizer.Region(-1000, -1000, 10000, 10000));

        Assert.Single(result.Walls);
        Assert.Equal(1, result.HatchesInRegion);
    }

    [Fact]
    public void A_short_strip_may_be_a_flat_column()
    {
        var result = new Scene().Box("FP_1", 0, 0, 1100, 300).Run();

        var wall = Assert.Single(result.Walls.Concat(result.Dropped));
        Assert.Contains(wall.Reasons, r => r.EndsWith(WallRecognizer.ShortReason));
        Assert.False(wall.Sure);
    }
    private static CaseSupport Cap(double x0, double y0, double x1, double y1) => new()
    {
        Kind = "cap", Cx = (x0 + x1) / 2, Cy = (y0 + y1) / 2, Points = [[x0, y0], [x1, y0], [x1, y1], [x0, y1]],
    };

    [Fact]
    public void Every_separate_area_of_one_hatch_is_read()
    {
        // One hatch, two areas: a 300 wall and, apart from it, an H of three 300 walls.
        var scene = new Scene();
        scene.Box("BETONG", 0, 0, 300, 4000);
        scene.Case.Hatches[0].Loops.Add([[2000, 0], [2300, 0], [2300, 1350], [4700, 1350], [4700, 0], [5000, 0], [5000, 3000],
            [4700, 3000], [4700, 1650], [2300, 1650], [2300, 3000], [2000, 3000]]);

        var result = scene.Run();

        Assert.Equal(4, result.Walls.Count);
        Assert.Equal(1500, Near(result, 3500, 1500).Y0, 1);
    }

    [Fact]
    public void A_band_inside_a_larger_hatched_area_is_not_a_wall()
    {
        // An L-shaped floor zone 3000 x 3000 with a 300 arm: the arm has hatch on one side, so it is part of the zone.
        var result = new Scene().Hatch("DOLMIT", 0, 0, 3000, 0, 3000, 300, 3300, 300, 3300, 3000, 0, 3000).Run();

        Assert.DoesNotContain(result.Walls, wall => wall.ThicknessMm == 300 && wall.Length > 2900);
    }

    [Fact]
    public void A_concrete_hatch_220_thick_is_not_masonry()
    {
        var result = new Scene().Box("BETONG", 0, 0, 2500, 220).Run();

        var wall = Assert.Single(result.Walls.Concat(result.Dropped));
        Assert.Contains("+10% " + WallRecognizer.ConcretePatternReason, wall.Reasons);
        Assert.DoesNotContain(wall.Reasons, reason => reason.EndsWith(WallRecognizer.MasonrySizeReason));
    }

    [Fact]
    public void A_wall_over_a_pile_cap_is_carried_and_one_over_none_is_reviewed()
    {
        var scene = new Scene().Box("BETONG", 0, 0, 300, 4000).Box("BETONG", 9000, 0, 9300, 4000);
        scene.Below.Add(Cap(-500, -500, 800, 4500));

        var result = scene.Run();

        var carried = Near(result, 150, 0);
        Assert.Contains("+20% " + WallRecognizer.FoundationReason, carried.Reasons);
        Assert.True(carried.Sure);
        var loose = Near(result, 9150, 0);
        Assert.Contains(Evidence.VetoReason(WallRecognizer.NoFoundationReason), loose.Reasons);
        Assert.False(loose.Sure);
        Assert.Equal(Evidence.VetoCapPercent, loose.Percent);
    }

    [Fact]
    public void Without_modeled_foundations_an_unsupported_wall_only_loses_points()
    {
        var wall = Assert.Single(new Scene().Box("BETONG", 0, 0, 300, 4000).Run().Walls);

        Assert.Contains("-20% " + WallRecognizer.UnsupportedReason, wall.Reasons);
        Assert.DoesNotContain(wall.Reasons, reason => reason.Contains(WallRecognizer.NoFoundationReason));
    }

    [Fact]
    public void A_long_column_hatch_inside_a_modeled_column_is_not_a_wall()
    {
        var result = new Scene().Box("SOLID", 0, 0, 350, 1400).Column(175, 700, 750).Run();

        Assert.Empty(result.Walls);
        Assert.Equal(1, result.Skipped["dải hatch nằm trong cột đã dựng"]);
    }
    [Fact]
    public void A_short_piece_in_line_with_a_wall_a_door_away_is_that_wall()
    {
        // A 300 wall, a 1100 door, then a 300 x 1000 piece on the same line; a lone 300 x 1000 elsewhere stays a column.
        var result = new Scene().Box("BETONG", 0, 0, 300, 3000).Box("BETONG", 0, 4100, 300, 5100)
            .Box("BETONG", 6000, 0, 6300, 1000).Run();

        var walls = result.Walls.Concat(result.Dropped).ToList();
        Assert.Equal(2, walls.Count);
        Assert.Contains(walls, wall => Math.Abs(wall.Y0 - 4100) < 1 || Math.Abs(wall.Y1 - 4100) < 1);
        Assert.Equal(1, result.Skipped.Single(pair => pair.Key.StartsWith("hatch cỡ cột")).Value);
    }
}
