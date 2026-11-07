using System.Text.Json;
using Bimtech.RevitMcp.Data;
using Bimtech.RevitMcp.Planning;
using Xunit;

namespace Bimtech.RevitMcp.Planning.Tests;

// Beams from clean data (beam/1.0, read through InnoCAD) instead of a CAD scan: reading, items per type, each beam
// named in Mark, flagged beams apart, beams without size and strips that are not beams left out.
public sealed class BeamDataTests
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Data", "MB_KC_Tang2_beam.json");
    private static BeamDataSet Fixture() => BeamDataReader.Parse(File.ReadAllText(FixturePath));

    private static readonly BeamTypeChoice Exact = new("555", null, "Type 350x600", null);
    private static readonly BeamTypeChoice Create = new(null, "777", "tạo Type 350x600 trong BTC-DamCN_BeTong", null);

    private static List<PlanItem> Plan(BeamDataSet data, Func<string, double, double, BeamTypeChoice>? choose = null, string? origin = null) =>
        BeamDataPlanner.BuildItems(data, 100, 200, "694", "Level 2", choose ?? ((_, _, _) => Exact), origin);

    private static List<JsonElement> Beams(List<PlanItem> items, string id) =>
        items.Single(i => i.ItemId == id).Params.GetProperty("beams").EnumerateArray().ToList();

    // ---- reading ----

    [Fact]
    public void The_real_file_reads_141_spans_123_to_build()
    {
        var data = Fixture();

        Assert.StartsWith("beam/1.", data.Source.Schema);
        Assert.Equal(("innocad-live", "MB_KC_Tang2.dwg", "Level 2", 0.0),
            (data.Source.Source, data.Source.Drawing, data.Source.LevelBase, data.Source.TopOffsetMm));
        Assert.Equal(141, data.Beams.Count);
        Assert.Equal(15, data.NotBeams);
        Assert.Equal(123, data.Buildable.Count);
        // The drawing names these without a size anywhere: not built, nothing guessed.
        Assert.Equal(["D2-Y8", "D2-Y15", "D2-Y15"], data.Unsized.Select(b => b.Name));
        var first = data.Beams[0];
        Assert.Equal(("D2-X21", "220x600", 220.0, 600.0, 44150.0, -1240.0, 47364.5, -1240.0),
            (first.Name, first.Type, first.B, first.H, first.X1, first.Y1, first.X2, first.Y2));
        Assert.Equal(("wall", "wall"), (first.SupportStart, first.SupportEnd));
    }

    [Theory]
    [InlineData("\"schema\":\"beam/1.0\"", "\"schema\":\"beam/2.0\"", "meta.schema")]
    [InlineData("\"unit\":\"mm\"", "\"unit\":\"inch\"", "meta.unit")]
    [InlineData("[\"D1\",\"220x600\",220,600", "[\"D1\",\"220x600\",0,600", "positive")]
    [InlineData("0,0,1000,0,1000", "0,0,0,0,1000", "no length")]
    public void Data_this_version_cannot_place_is_refused(string from, string to, string field)
    {
        var error = Assert.Throws<DataFileException>(() => BeamDataReader.Parse(Minimal().Replace(from, to)));
        Assert.Contains(field, error.Message);
    }

    // ---- review items ----

    [Fact]
    public void One_item_per_type_each_beam_named_and_flagged_beams_apart()
    {
        var items = Plan(Fixture());

        Assert.Equal(["B-220x300", "B-220x300-xem", "B-220x400", "B-220x500", "B-220x600", "B-220x600-xem", "B-250x400",
            "B-250x500", "B-300x500", "B-300x600", "B-330x600", "B-350x600", "B-400x600", "B-450x600"], items.Select(i => i.ItemId));
        Assert.Equal(123, items.Sum(i => i.Params.GetProperty("beams").GetArrayLength()));
        Assert.All(items, item =>
        {
            Assert.Equal("create_beams", item.Action);
            Assert.Equal(PlanTier.Review, item.Tier);
            Assert.Equal(item.ItemId.EndsWith("-xem") ? BeamDataPlanner.UncheckedConfidence : BeamDataPlanner.CheckedConfidence, item.Confidence);
            Assert.Equal(("694", 0.0), (item.Params.GetProperty("levelId").GetString(), item.Params.GetProperty("offsetMm").GetDouble()));
            Assert.All(item.Params.GetProperty("beams").EnumerateArray(), b => Assert.False(string.IsNullOrEmpty(b.GetProperty("mark").GetString())));
        });
        // Width drawn 200 but named 220: the reviewer decides.
        Assert.Equal("D2-Y4", Assert.Single(Beams(items, "B-220x600-xem")).GetProperty("mark").GetString());
        Assert.Contains(items.Single(i => i.ItemId == "B-220x600-xem").Reasons, r => r.Contains("rộng đo lệch tên"));
    }

    [Fact]
    public void Lift_shaft_beams_take_their_size_from_the_other_line_of_the_name()
    {
        var items = Plan(Fixture());

        // D2-X13 / D2-X13A: two solid lines over the shaft, "220x400)" written on the other side of the beam.
        var shaft = Beams(items, "B-220x400");
        Assert.Equal(5, shaft.Count);
        Assert.Equal(["D2-X13", "D2-X13", "D2-X13", "D2-X13A", "D2-X13A"], shaft.Select(b => b.GetProperty("mark").GetString()).Order());
        // Wall centre to wall centre across the shaft (model = data + base point 100, 200).
        Assert.Contains(shaft, b => (b.GetProperty("x0").GetDouble(), b.GetProperty("x1").GetDouble(), b.GetProperty("y0").GetDouble())
            == (35650.0, 38150.0, 1260.0));
        Assert.Contains(items.Single(i => i.ItemId == "B-220x400").Reasons, r => r.Contains("dòng kia của tên"));
    }

    [Fact]
    public void A_type_to_create_or_a_missing_type_or_a_shifted_origin_lowers_or_blocks_the_item()
    {
        var created = Plan(Fixture(), (_, _, _) => Create);
        Assert.All(created, i => Assert.Equal(i.ItemId.EndsWith("-xem") ? BeamDataPlanner.UncheckedConfidence : BeamDataPlanner.LookConfidence, i.Confidence));
        Assert.All(created, i => Assert.Equal("777", i.Params.GetProperty("templateFamilyId").GetString()));

        var blocked = Plan(Fixture(), (_, _, _) => new BeamTypeChoice(null, null, "chưa có Type", "Chưa có Type dầm"));
        Assert.All(blocked, i => Assert.Equal((PlanTier.Blocked, "Chưa có Type dầm"), (i.Tier, i.Reasons[0])));

        var shifted = Plan(Fixture(), origin: "model chưa có trục A");
        Assert.All(shifted, i => Assert.Equal(BeamDataPlanner.UncheckedConfidence, i.Confidence));
    }

    private static string Minimal() =>
        """
        {"meta":{"schema":"beam/1.0","unit":"mm","origin":"A-1","level":{"base":"Level 2","top":"next"},"top_offset_mm":0},
         "beams":{"cols":["name","type","b","h","width","x1","y1","x2","y2","length","dir","support_start","support_end","grid_start","grid_end","flags"],
                  "rows":[["D1","220x600",220,600,220,0,0,1000,0,1000,"X","column","column","A-1","A-2",[]]]},
         "issues":[],"decisions":[]}
        """;
}
