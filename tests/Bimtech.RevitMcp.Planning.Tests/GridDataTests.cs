using System.Text.Json;
using Bimtech.RevitMcp.Data;
using Bimtech.RevitMcp.Planning;
using Xunit;

namespace Bimtech.RevitMcp.Planning.Tests;

// Grids from clean data (grid/1.0) instead of a CAD import: reading, placing on the base point, review items.
public sealed class GridDataTests
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Data", "MB_KC_Tang2_grid.json");
    private static GridDataSet Fixture() => GridDataReader.Parse(File.ReadAllText(FixturePath));

    // ---- reading ----

    [Fact]
    public void The_real_file_reads_17_grids_with_names_positions_and_handles()
    {
        var data = Fixture();

        Assert.StartsWith("grid/1.", data.Source.Schema);
        Assert.Equal("MB_KC_Tang2.dxf", data.Source.Src);
        Assert.Equal("A-1", data.Source.Origin);
        Assert.True(data.SelfCheck);
        Assert.Equal(17, data.Grids.Count);
        Assert.Equal(12, data.Grids.Count(g => g.Axis == "X"));
        Assert.Equal(["1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12"],
            data.Grids.Where(g => g.Axis == "X").Select(g => g.Name));
        var aux = Assert.Single(data.Grids, g => g.Name == "A'");
        Assert.Equal(("Y", "aux", -1800.0, "fix"), (aux.Axis, aux.Kind, aux.PosMm, aux.Bubble));
        var g12 = Assert.Single(data.Grids, g => g.Name == "12");
        Assert.Equal((76100.0, "BAB/2B1"), (g12.PosMm, g12.Handle));
    }

    [Theory]
    [InlineData("\"schema\":\"grid/2.0\"", "meta.schema")]
    [InlineData("\"unit\":\"inch\"", "meta.unit")]
    [InlineData("\"rotation_deg\":1.5", "meta.rotation_deg")]
    public void Data_this_version_cannot_place_is_refused(string replacement, string field)
    {
        var json = Minimal().Replace(replacement.Split(':')[0] + ":" + Original(replacement), replacement);
        var error = Assert.Throws<GridDataException>(() => GridDataReader.Parse(json));
        Assert.Contains(field, error.Message);
    }

    [Fact]
    public void A_missing_column_is_named_in_the_error()
    {
        var json = Minimal().Replace("\"pos\",", "");
        var error = Assert.Throws<GridDataException>(() => GridDataReader.Parse(json));
        Assert.Contains("'pos'", error.Message);
    }

    [Fact]
    public void Text_that_is_not_json_is_refused()
    {
        Assert.Throws<GridDataException>(() => GridDataReader.Parse("not json"));
    }

    // ---- layout ----

    [Fact]
    public void A_vertical_grid_runs_from_the_plain_end_to_the_bubble_end_on_the_base_point()
    {
        var grid = new DataGrid("1", "X", "main", 0, -7096.1, 21594.9, "start", "BAB/2A6", "BAB/2C6");

        var line = GridDataLayout.ToModelLine(grid, baseXmm: 100, baseYmm: 200);

        // Bubble at data start (bottom, y = -7096.1) -> Revit head at the line's end point.
        Assert.Equal(new GridDataLayout.ModelLine(100, 200 + 21594.9, 100, 200 - 7096.1), line);
    }

    [Fact]
    public void A_horizontal_grid_sits_at_its_y_position_from_the_base_point()
    {
        var grid = new DataGrid("B", "Y", "main", 6400, -1130.9, 79683.9, "start", "BAB/2A3", null);

        var line = GridDataLayout.ToModelLine(grid, baseXmm: 0, baseYmm: 0);

        Assert.Equal(new GridDataLayout.ModelLine(79683.9, 6400, -1130.9, 6400), line);
    }

    [Fact]
    public void A_grid_without_bubble_keeps_the_data_direction()
    {
        var grid = new DataGrid("A'", "Y", "aux", -1800, -1130.9, 79683.9, "fix", "BAB/2A2", null);

        var line = GridDataLayout.ToModelLine(grid, 0, 0);

        Assert.Equal(new GridDataLayout.ModelLine(-1130.9, -1800, 79683.9, -1800), line);
    }

    // ---- review items ----

    [Fact]
    public void Every_grid_of_the_real_file_becomes_one_sure_create_grid_item()
    {
        var items = GridDataPlanner.BuildItems(Fixture(), 0, 0, new HashSet<string>(), pin: true);

        Assert.Equal(17, items.Count);
        Assert.All(items, item =>
        {
            Assert.Equal("create_grid", item.Action);
            Assert.Equal(PlanTier.Review, item.Tier);
            Assert.Equal(GridDataPlanner.CheckedConfidence, item.Confidence);
            Assert.True(item.Params.GetProperty("pin").GetBoolean());
        });
        var g1 = Params(items, "G-1");
        Assert.Equal("1", g1.GetProperty("name").GetString());
        Assert.Contains("BAB/2A6", g1.GetProperty("comments").GetString());
        Assert.Contains("MB_KC_Tang2.dxf", g1.GetProperty("comments").GetString());
        var aux = Assert.Single(items, item => item.ItemId == "G-A'");
        Assert.Contains(aux.Reasons, reason => reason.Contains("MB_KC_Tang2_fix.json"));
    }

    [Fact]
    public void A_name_already_in_the_model_is_blocked()
    {
        var items = GridDataPlanner.BuildItems(Fixture(), 0, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "b" }, pin: true);

        var b = Assert.Single(items, item => item.ItemId == "G-B");
        Assert.Equal(PlanTier.Blocked, b.Tier);
        Assert.Equal(16, items.Count(item => item.Tier == PlanTier.Review));
    }

    [Fact]
    public void Unnamed_and_twice_named_grids_are_blocked_with_their_own_item_ids()
    {
        var data = Fixture() with
        {
            Grids =
            [
                new DataGrid("?", "Y", "main", -1800, 0, 1000, "none", "H1", null),
                new DataGrid("C", "Y", "main", 100, 0, 1000, "start", "H2", null),
                new DataGrid("C", "Y", "main", 200, 0, 1000, "start", "H3", null),
            ],
        };

        var items = GridDataPlanner.BuildItems(data, 0, 0, new HashSet<string>(), pin: true);

        Assert.All(items, item => Assert.Equal(PlanTier.Blocked, item.Tier));
        Assert.Equal(["G-?@-1800", "G-C@100", "G-C@200"], items.Select(item => item.ItemId));
    }

    [Fact]
    public void Data_that_failed_its_self_check_is_left_to_the_reviewer()
    {
        var items = GridDataPlanner.BuildItems(Fixture() with { SelfCheck = false }, 0, 0, new HashSet<string>(), pin: true);

        Assert.All(items, item => Assert.Equal(GridDataPlanner.UncheckedConfidence, item.Confidence));
        Assert.True(GridDataPlanner.UncheckedConfidence < PlanConfidence.AutoApproveFrom);
    }

    // ---- helpers ----

    private static JsonElement Params(List<PlanItem> items, string itemId) =>
        Assert.Single(items, item => item.ItemId == itemId).Params;

    private static string Minimal() =>
        """
        {"meta":{"schema":"grid/1.0","unit":"mm","rotation_deg":0.0,"origin":"A-1","origin_cad":[0,0]},
         "grids":{"cols":["name","axis","pos","start","end"],"rows":[["1","X",0,0,1000]]},
         "stats":{"self_check":true}}
        """;

    // The value Minimal() has for the field named in a replacement like "\"unit\":\"inch\"".
    private static string Original(string replacement) => replacement.Split(':')[0] switch
    {
        "\"schema\"" => "\"grid/1.0\"",
        "\"unit\"" => "\"mm\"",
        "\"rotation_deg\"" => "0.0",
        _ => throw new ArgumentException(replacement),
    };
}
