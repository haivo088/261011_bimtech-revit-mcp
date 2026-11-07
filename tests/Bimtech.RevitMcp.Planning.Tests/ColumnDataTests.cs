using System.Text.Json;
using Bimtech.RevitMcp.Data;
using Bimtech.RevitMcp.Planning;
using Xunit;

namespace Bimtech.RevitMcp.Planning.Tests;

// Columns from clean data (column/1.0) instead of a CAD scan: reading, review items per type, kinds, existing columns.
public sealed class ColumnDataTests
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Data", "MB_KC_Tang2_column.json");
    private static ColumnDataSet Fixture() => ColumnDataReader.Parse(File.ReadAllText(FixturePath));

    private static readonly ColumnTypeChoice Existing = new("777", null, "Type 350x1400", null);
    private static readonly ColumnTypeChoice NewType = new(null, "888", "Type mới trong BTC-CotCN_BeTong", null);

    private static ColumnDataPlanner.Result Plan(ColumnDataSet data, Func<string, double, double, ColumnTypeChoice>? choose = null,
        Func<double, double, bool>? existing = null, string? origin = null) =>
        ColumnDataPlanner.BuildItems(data, 100, 200, "311", "694", choose ?? ((_, _, _) => Existing), existing ?? ((_, _) => false), origin);

    // ---- reading ----

    [Fact]
    public void The_real_file_reads_51_checked_visible_columns_on_level_2()
    {
        var data = Fixture();

        Assert.StartsWith("column/1.", data.Source.Schema);
        Assert.Equal(("Level 2", "next", "MB_KC_Tang2_grid.json"), (data.Source.LevelBase, data.Source.LevelTop, data.Source.GridFile));
        Assert.Equal(51, data.Columns.Count);
        Assert.All(data.Columns, c => Assert.Equal("visible", c.Kind));
        Assert.True(data.IsChecked);
        Assert.Equal(51, data.Checks.DynSizeOk);
        Assert.Equal(7, data.WallLike);
        Assert.Equal(
            new Dictionary<string, int> { ["C1000x1000"] = 18, ["C350x1300"] = 4, ["C350x1400"] = 28, ["C500x1700"] = 1 },
            data.Columns.GroupBy(c => c.Type).ToDictionary(g => g.Key, g => g.Count()));
    }

    [Fact]
    public void The_long_side_runs_along_x_at_0_degrees_and_along_y_at_90()
    {
        var along0 = new DataColumn("C350x1400", "visible", 0, 0, 350, 1400, 0, "A-1", null, null);
        var along90 = along0 with { RotationDeg = 90 };

        Assert.Equal((1400.0, 350.0), (along0.SizeXmm, along0.SizeYmm));
        Assert.Equal((350.0, 1400.0), (along90.SizeXmm, along90.SizeYmm));
    }

    [Theory]
    [InlineData("\"schema\":\"column/1.0\"", "\"schema\":\"column/2.0\"", "meta.schema")]
    [InlineData("\"unit\":\"mm\"", "\"unit\":\"inch\"", "meta.unit")]
    [InlineData(",0,\"A-1\"", ",45,\"A-1\"", "rotation_deg")]
    [InlineData("350,1400", "0,1400", "positive")]
    public void Data_this_version_cannot_place_is_refused(string from, string to, string field)
    {
        var error = Assert.Throws<DataFileException>(() => ColumnDataReader.Parse(Minimal().Replace(from, to)));
        Assert.Contains(field, error.Message);
    }

    // ---- review items ----

    [Fact]
    public void One_item_per_type_holds_every_column_in_model_coordinates()
    {
        var result = Plan(Fixture());

        Assert.Equal(["K-C1000x1000", "K-C350x1300", "K-C350x1400", "K-C500x1700"], result.Items.Select(i => i.ItemId));
        Assert.Equal(51, result.Items.Sum(i => i.Params.GetProperty("columns").GetArrayLength()));
        Assert.All(result.Items, item =>
        {
            Assert.Equal("create_columns", item.Action);
            Assert.Equal(PlanTier.Review, item.Tier);
            Assert.Equal(ColumnDataPlanner.CheckedConfidence, item.Confidence);
            Assert.Equal("311", item.Params.GetProperty("baseLevelId").GetString());
            Assert.Equal("777", item.Params.GetProperty("typeId").GetString());
        });
        // First C350x1400 of the file: centre (6750, 175) from A-1, long side along X.
        var column = Columns(result, "K-C350x1400").First(c => c.GetProperty("at").GetString() == "A-2(+50,+175)");
        Assert.Equal((6850.0, 375.0), (column.GetProperty("x").GetDouble(), column.GetProperty("y").GetDouble()));
        Assert.Equal((1400.0, 350.0, 0.0), (column.GetProperty("lengthMm").GetDouble(), column.GetProperty("widthMm").GetDouble(),
            column.GetProperty("angleDeg").GetDouble()));
        Assert.Equal((350.0, 1400.0), (Params(result, "K-C350x1400").GetProperty("sizeAMm").GetDouble(),
            Params(result, "K-C350x1400").GetProperty("sizeBMm").GetDouble()));
    }

    [Fact]
    public void Columns_already_in_the_model_are_skipped_and_counted()
    {
        var result = Plan(Fixture(), existing: (x, y) => x == 6850 && y == 375);

        Assert.Equal(1, result.SkippedExisting);
        Assert.Equal(27, Columns(result, "K-C350x1400").Count);
        Assert.Contains(result.Items.Single(i => i.ItemId == "K-C350x1400").Reasons, r => r.Contains("1 cột đã có"));
    }

    [Fact]
    public void A_type_to_create_needs_a_look_and_a_missing_type_blocks()
    {
        var created = Plan(Fixture(), choose: (_, _, _) => NewType);
        Assert.All(created.Items, item =>
        {
            Assert.Equal(ColumnDataPlanner.LookConfidence, item.Confidence);
            Assert.Equal("888", item.Params.GetProperty("templateFamilyId").GetString());
        });
        Assert.True(ColumnDataPlanner.LookConfidence < PlanConfidence.AutoApproveFrom);

        var blocked = Plan(Fixture(), choose: (_, _, _) => new ColumnTypeChoice(null, null, "chưa có Type", "no family"));
        Assert.All(blocked.Items, item =>
        {
            Assert.Equal(PlanTier.Blocked, item.Tier);
            Assert.Equal("no family", item.Reasons[0]);
        });
    }

    [Fact]
    public void Hidden_columns_are_not_built_and_planted_ones_get_their_own_item()
    {
        var data = Fixture();
        var cols = data.Columns.ToList();
        cols[0] = cols[0] with { Kind = "hidden" };
        cols[1] = cols[1] with { Kind = "planted" };

        var result = Plan(data with { Columns = cols });

        Assert.Single(result.Hidden);
        var planted = Assert.Single(result.Items, i => i.ItemId == "K-" + cols[1].Type + "-cay");
        Assert.Equal(ColumnDataPlanner.UncheckedConfidence, planted.Confidence);
        Assert.Equal(49, result.Items.Where(i => !i.ItemId.EndsWith("-cay")).Sum(i => i.Params.GetProperty("columns").GetArrayLength()));
    }

    [Fact]
    public void A_model_grid_off_the_data_origin_leaves_every_group_to_the_reviewer()
    {
        var result = Plan(Fixture(), origin: "trục A cách Project Base Point 1800 mm");

        Assert.All(result.Items, item =>
        {
            Assert.Equal(ColumnDataPlanner.UncheckedConfidence, item.Confidence);
            Assert.Contains(item.Reasons, r => r.Contains("1800 mm"));
        });
    }

    // ---- helpers ----

    private static JsonElement Params(ColumnDataPlanner.Result result, string itemId) =>
        result.Items.Single(i => i.ItemId == itemId).Params;

    private static List<JsonElement> Columns(ColumnDataPlanner.Result result, string itemId) =>
        Params(result, itemId).GetProperty("columns").EnumerateArray().ToList();

    private static string Minimal() =>
        """
        {"meta":{"schema":"column/1.0","unit":"mm","level":{"base":"Level 2","top":"next"}},
         "columns":{"cols":["type","kind","x","y","b","h","rotation_deg","grid_ref"],
                    "rows":[["C350x1400","visible",0,0,350,1400,0,"A-1"]]},
         "checks":{"rectangles":true,"no_duplicates":true,"dyn_size_ok":1,"overlaps":0}}
        """;
}
