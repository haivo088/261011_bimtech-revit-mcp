using System.Text.Json;
using Bimtech.RevitMcp.Data;
using Bimtech.RevitMcp.Planning;
using Xunit;

namespace Bimtech.RevitMcp.Planning.Tests;

// Slabs from clean data (slab/1.0, read through InnoCAD): reading, one create_floor item per slab with its openings
// as holes, type choice and origin check lowering or blocking the item.
public sealed class SlabDataTests
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Data", "MB_KC_Tang2_slab.json");
    private static SlabDataSet Fixture() => SlabDataReader.Parse(File.ReadAllText(FixturePath));

    private static readonly SlabTypeChoice Exact = new("555", "Type BTC_SanKC_120", null);
    private static readonly SlabTypeChoice Create = new(null, "tạo Type BTC_SanKC_120", null);

    private static List<PlanItem> Plan(SlabDataSet data, SlabTypeChoice? choice = null, string? origin = null) =>
        SlabDataPlanner.BuildItems(data, 100, 200, "694", "Level 2", _ => choice ?? Exact, origin);

    [Fact]
    public void The_real_file_reads_the_main_slab_and_one_slab_per_hatch_zone()
    {
        var data = Fixture();

        Assert.Equal("slab/1.1", data.Source.Schema);
        Assert.Equal(("innocad-live", "MB_KC_Tang2.dwg", "Level 2"), (data.Source.Source, data.Source.Drawing, data.Source.LevelBase));
        Assert.Equal(10, data.Slabs.Count);
        Assert.Equal(["main", "ANGLE", "ANSI31", "ANSI37", "ANSI37", "ANSI37", "BETONG", "BETONG", "DOLMIT", "DOLMIT"],
            data.Slabs.Select(s => s.Zone));
        Assert.All(data.Slabs, s => Assert.Equal((120.0, 0.0), (s.Thickness, s.TopOffset)));
        Assert.Equal(32, data.Openings.Count);
        Assert.All(data.Openings, o => Assert.True(o.CanCut));
        // Main + zones, inner loops off, cover the slab exactly once: 1567.95 m2 as the single slab before.
        Assert.Equal(1567.95, Math.Round(data.Slabs.Sum(s => Area(s.Points) - s.Holes!.Sum(Area)) / 1e6, 2));
        // The edge strips and the A' extension are BETONG slabs; the main slab no longer reaches the edge at x = -220.
        Assert.Equal(-220.0, Math.Round(data.Slabs.Where(s => s.Zone == "BETONG").Min(s => s.Points.Min(p => p.X))));
    }

    private static double Area(IReadOnlyList<(double X, double Y)> p) =>
        Math.Abs(Enumerable.Range(0, p.Count).Sum(i => p[i].X * p[(i + 1) % p.Count].Y - p[(i + 1) % p.Count].X * p[i].Y)) / 2;

    [Theory]
    [InlineData("\"schema\":\"slab/1.0\"", "\"schema\":\"slab/2.0\"", "meta.schema")]
    [InlineData("\"unit\":\"mm\"", "\"unit\":\"inch\"", "meta.unit")]
    [InlineData("[\"S1\",\"main\",120,", "[\"S1\",\"main\",0,", "thickness")]
    [InlineData("[[0,0],[1000,0],[1000,1000],[0,1000]]]]}", "[[0,0],[1000,0]]]]}", "3 points")]
    public void Data_this_version_cannot_place_is_refused(string from, string to, string field)
    {
        var error = Assert.Throws<DataFileException>(() => SlabDataReader.Parse(Minimal().Replace(from, to)));
        Assert.Contains(field, error.Message);
    }

    [Fact]
    public void One_floor_item_per_slab_with_its_inner_loops_as_holes_and_the_zone_in_comments()
    {
        var items = Plan(Fixture());

        Assert.Equal(Enumerable.Range(1, 10).Select(i => $"F-S{i}"), items.Select(i => i.ItemId));
        Assert.All(items, i => Assert.Equal(("create_floor", PlanTier.Review, SlabDataPlanner.CheckedConfidence), (i.Action, i.Tier, i.Confidence)));
        Assert.Equal(JsonValueKind.Null, items[0].Params.GetProperty("comment").ValueKind);
        Assert.Equal("Vùng DOLMIT", items.Single(i => i.ItemId == "F-S9").Params.GetProperty("comment").GetString());
        var item = items[0];
        var p = item.Params;
        Assert.Equal(("694", 0.0, "555", 120.0),
            (p.GetProperty("levelId").GetString(), p.GetProperty("offsetMm").GetDouble(), p.GetProperty("floorTypeId").GetString(),
                p.GetProperty("thicknessMm").GetDouble()));
        // Main slab: its openings and the zone slabs inside it.
        Assert.Equal(20, p.GetProperty("holes").GetArrayLength());
        // Model coordinates = data + base point (100, 200).
        var first = p.GetProperty("points").EnumerateArray().Select(q => (q[0].GetDouble(), q[1].GetDouble())).ToList();
        var data0 = Fixture().Slabs[0].Points[0];
        Assert.Equal((Math.Round(data0.X + 100, 2), Math.Round(data0.Y + 200, 2)), first[0]);
        Assert.Contains(item.Reasons, r => r.StartsWith("20 vòng trong"));
    }

    [Fact]
    public void A_type_to_create_or_a_missing_seed_or_a_shifted_origin_lowers_or_blocks_the_item()
    {
        var created = Plan(Fixture(), Create)[0];
        Assert.Equal(SlabDataPlanner.LookConfidence, created.Confidence);
        Assert.Equal(JsonValueKind.Null, created.Params.GetProperty("floorTypeId").ValueKind);

        var blocked = Plan(Fixture(), new SlabTypeChoice(null, "chưa có Type", "Model chưa có Floor Type"))[0];
        Assert.Equal((PlanTier.Blocked, "Model chưa có Floor Type"), (blocked.Tier, blocked.Reasons[0]));

        Assert.All(Plan(Fixture(), origin: "model chưa có trục A"), i => Assert.Equal(SlabDataPlanner.UncheckedConfidence, i.Confidence));
    }

    [Fact]
    public void An_opening_on_the_slab_edge_is_reported_not_cut()
    {
        var data = SlabDataReader.Parse(Minimal("[\"on_edge\"]"));

        var item = Assert.Single(Plan(data));
        Assert.Equal(0, item.Params.GetProperty("holes").GetArrayLength());
        Assert.Equal(SlabDataPlanner.UncheckedConfidence, item.Confidence);
        Assert.Contains(item.Reasons, r => r.Contains("chạm mép sàn"));
    }

    private static string Minimal(string flags = "[]") =>
        """
        {"meta":{"schema":"slab/1.0","unit":"mm","origin":"A-1","level":{"base":"Level 2","top":"next"}},
         "openings":{"cols":["id","slab","kind","area_m2","grid_ref","flags","points"],
                     "rows":[["L1","S1","duct",0.04,"A-1",FLAGS,[[400,400],[600,400],[600,600],[400,600]]]]},
         "slabs":{"cols":["id","zone","thickness","top_offset","area_m2","grid_ref","openings","points"],
                  "rows":[["S1","main",120,0,1.0,"A-1",["L1"],[[0,0],[1000,0],[1000,1000],[0,1000]]]]},
         "issues":[],"decisions":[]}
        """.Replace("FLAGS", flags);
}
