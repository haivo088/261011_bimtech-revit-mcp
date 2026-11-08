using System.Text.Json;
using Bimtech.RevitMcp.Data;
using Bimtech.RevitMcp.Planning;
using Xunit;

namespace Bimtech.RevitMcp.Planning.Tests;

// Walls from clean data (wall/1.0, read through InnoCAD) instead of a CAD scan: reading, items per type, flagged walls.
public sealed class WallDataTests
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Data", "MB_KC_Tang2_wall.json");
    private static WallDataSet Fixture() => WallDataReader.Parse(File.ReadAllText(FixturePath));

    private static readonly WallTypeChoice Exact = new("555", null, false, "Type BTC-VachKC_300", null);
    private static readonly WallTypeChoice Create = new(null, "666", false, "Type mới BTC-VachKC_300", null);

    private static List<PlanItem> Plan(WallDataSet data, Func<string, double, WallTypeChoice>? choose = null, string? origin = null) =>
        WallDataPlanner.BuildItems(data, 100, 200, "694", "354083", choose ?? ((_, _) => Exact), origin);

    // ---- reading ----

    [Fact]
    public void The_real_file_reads_15_walls_and_knows_which_outline_did_not_add_up()
    {
        var data = Fixture();

        Assert.StartsWith("wall/1.", data.Source.Schema);
        Assert.Equal(("innocad-live", "MB_KC_Tang2.dwg", "Level 2", "next"),
            (data.Source.Source, data.Source.Drawing, data.Source.LevelBase, data.Source.LevelTop));
        Assert.Equal(15, data.Walls.Count);
        // The 331 x 750 piece was drawn 1 mm off; snapped to the 5 mm module (cadlib.snap) it is V330 and adds up.
        Assert.Empty(data.BadOutlines);
        Assert.Equal(0, data.Slivers);
        var first = data.Walls[0];
        Assert.Equal(("V300", 300.0, 35550.0, -1200.0, 35550.0, 8800.0), (first.Type, first.Thickness, first.X1, first.Y1, first.X2, first.Y2));
        Assert.True(data.IsChecked(first));
        Assert.Equal("V330", Assert.Single(data.Walls, w => w.Outline == "O13").Type);
    }

    [Theory]
    [InlineData("\"schema\":\"wall/1.0\"", "\"schema\":\"wall/2.0\"", "meta.schema")]
    [InlineData("\"unit\":\"mm\"", "\"unit\":\"inch\"", "meta.unit")]
    [InlineData("[\"V300\",300,0,0,0,1000", "[\"V300\",0,0,0,0,1000", "thickness")]
    [InlineData("[\"V300\",300,0,0,0,1000", "[\"V300\",300,0,0,0,0", "no length")]
    public void Data_this_version_cannot_place_is_refused(string from, string to, string field)
    {
        var error = Assert.Throws<DataFileException>(() => WallDataReader.Parse(Minimal().Replace(from, to)));
        Assert.Contains(field, error.Message);
    }

    // ---- review items ----

    [Fact]
    public void One_item_per_type_and_flagged_walls_apart()
    {
        var items = Plan(Fixture());

        Assert.Equal(["W-V220", "W-V300", "W-V330"], items.Select(i => i.ItemId));
        Assert.Equal(15, items.Sum(i => i.Params.GetProperty("lines").GetArrayLength()));
        Assert.Equal(12, Lines(items, "W-V300").Count);
        Assert.All(items, item =>
        {
            Assert.Equal("create_walls", item.Action);
            Assert.Equal(PlanTier.Review, item.Tier);
            Assert.Equal(item.ItemId.EndsWith("-xem") ? WallDataPlanner.UncheckedConfidence : WallDataPlanner.CheckedConfidence, item.Confidence);
            Assert.Equal(("694", "354083"), (item.Params.GetProperty("baseLevelId").GetString(), item.Params.GetProperty("topLevelId").GetString()));
        });
        Assert.Contains(items.Single(i => i.ItemId == "W-V330").Reasons, r => r.Contains("Đã kiểm tra"));
    }

    [Fact]
    public void Walls_of_other_outlines_join_and_a_stub_continues_its_wall()
    {
        var walls = Fixture().Walls;

        // The 220 strips between two 300 walls (other outlines) run to their centrelines: T joins on both ends.
        Assert.Contains(walls, w => w.Type == "V220" && (w.X1, w.X2) == (41350.0, 44150.0));
        // The 300 x 260 piece under the vertical wall at x = 44150 is part of that wall, not a V260 wall; the wall now
        // runs through its door gap (6050..7350) in one piece.
        Assert.DoesNotContain(walls, w => w.Type == "V260");
        Assert.Contains(walls, w => w.Type == "V300" && (w.X1, w.Y1, w.X2, w.Y2) == (44150.0, -1200.0, 44150.0, 8800.0));
    }

    [Fact]
    public void Door_gaps_bridged_by_the_extraction_become_openings_of_one_wall()
    {
        var data = Fixture();

        var lift = Assert.Single(data.Walls, w => (w.X1, w.Y1, w.X2, w.Y2) == (38050.0, -1200.0, 38050.0, 8800.0));
        Assert.Equal(4, lift.Openings.Count);
        Assert.All(lift.Openings, o => Assert.Equal((0.0, 2400.0, 1100.0), (o.Sill, o.Head!.Value, o.Y2 - o.Y1)));
        Assert.Equal(10, data.Walls.Sum(w => w.Openings.Count));
        // Lower core: the 300 x 300 return under the top wall is the top of the vertical wall (not a horizontal sliver),
        // so the lift door between it and the wall below is found: one wall 11350..15800 with two doors.
        var lower = Assert.Single(data.Walls, w => (w.X1, w.Y1, w.X2, w.Y2) == (38050.0, 11350.0, 38050.0, 15800.0));
        Assert.Equal([(12200.0, 13300.0), (14250.0, 15350.0)], lower.Openings.Select(o => (o.Y1, o.Y2)));

        var items = Plan(data);
        var openings = items.Single(i => i.ItemId == "W-V300").Params.GetProperty("openings").EnumerateArray()
            .Select(o => o.EnumerateArray().Select(v => v.GetDouble()).ToArray()).ToList();
        Assert.Equal(10, openings.Count);
        Assert.Contains([38150.0, -375.0, 38150.0, 725.0, 0.0, 2400.0], openings);   // base point (100, 200) added
        Assert.Contains(items.Single(i => i.ItemId == "W-V300").Reasons, r => r.StartsWith("10 cửa"));
    }

    [Fact]
    public void An_opening_without_head_height_is_bridged_but_not_cut()
    {
        var data = Fixture();
        var walls = data.Walls.Select(w => w with { Openings = w.Openings.Select(o => o with { Head = null }).ToList() }).ToList();

        var item = Plan(data with { Walls = walls }).Single(i => i.ItemId == "W-V300");

        Assert.Equal(0, item.Params.GetProperty("openings").GetArrayLength());
        Assert.Contains(item.Reasons, r => r.Contains("head_mm"));
    }

    [Fact]
    public void Centrelines_are_moved_onto_the_base_point()
    {
        var items = Plan(Fixture());

        var line = Lines(items, "W-V300")[0];
        Assert.Equal([35650.0, -1000.0, 35650.0, 9000.0], line);
        Assert.Equal(300.0, items.Single(i => i.ItemId == "W-V300").Params.GetProperty("thicknessMm").GetDouble());
    }

    [Fact]
    public void A_type_to_create_needs_a_look_and_a_missing_template_blocks_with_its_reason_first()
    {
        var created = Plan(Fixture(), (_, _) => Create);
        Assert.All(created.Where(i => !i.ItemId.EndsWith("-xem")), i =>
        {
            Assert.Equal(WallDataPlanner.LookConfidence, i.Confidence);
            Assert.Equal("666", i.Params.GetProperty("templateTypeId").GetString());
        });

        var blocked = Plan(Fixture(), (_, _) => new WallTypeChoice(null, null, false, "chưa có Type", "no template"));
        Assert.All(blocked, i =>
        {
            Assert.Equal(PlanTier.Blocked, i.Tier);
            Assert.Equal("no template", i.Reasons[0]);
        });
    }

    [Fact]
    public void A_model_grid_off_the_data_origin_leaves_every_group_to_the_reviewer()
    {
        var items = Plan(Fixture(), origin: "model chưa có trục A");

        Assert.All(items, i => Assert.Equal(WallDataPlanner.UncheckedConfidence, i.Confidence));
    }

    // ---- helpers ----

    private static List<double[]> Lines(List<PlanItem> items, string itemId) =>
        items.Single(i => i.ItemId == itemId).Params.GetProperty("lines").EnumerateArray()
            .Select(l => l.EnumerateArray().Select(v => v.GetDouble()).ToArray()).ToList();

    private static string Minimal() =>
        """
        {"meta":{"schema":"wall/1.0","unit":"mm","level":{"base":"Level 2","top":"next"}},
         "walls":{"cols":["type","thickness","x1","y1","x2","y2","grid_start","grid_end"],
                  "rows":[["V300",300,0,0,0,1000,"A-1","A-1(+0,+1000)"]]}}
        """;
}
