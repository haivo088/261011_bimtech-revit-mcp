using Bimtech.RevitMcp.Recognition;
using Xunit;

namespace Bimtech.RevitMcp.Recognition.Tests;

public sealed class StoreyNamesTests
{
    [Theory]
    [InlineData("KC-03 MB TANG 2 (+3.600).dwg", "Tầng 2", 3600.0)]
    [InlineData("Mặt bằng kết cấu tầng 10 +36,000.dwg", "Tầng 10", 36000.0)]
    [InlineData("MB_KC_T3_COS 7.20.dwg", "Tầng 3", 7200.0)]
    [InlineData("KC-01 MAT BANG MONG (-1.500).dwg", "Móng", -1500.0)]
    [InlineData("MB TẦNG HẦM 2 -6.600.dwg", "Hầm 2", -6600.0)]
    [InlineData("MB TANG TRET ±0.000.dwg", "Tầng trệt", 0.0)]
    [InlineData("MB MÁI.dwg", "Mái", null)]
    [InlineData("MB LỬNG +2.400.dwg", "Tầng lửng", 2400.0)]
    public void File_names_give_storey_name_and_elevation(string file, string name, double? elevationMm)
    {
        var guess = StoreyNames.Parse(file);

        Assert.Equal(name, guess.Name);
        Assert.Equal(elevationMm, guess.ElevationMm);
        Assert.True(guess.LooksLikePlan);
    }

    [Theory]
    [InlineData("KC-2.01 MB TANG 2.dwg")]
    [InlineData("KC-301 MB TANG 3.dwg")]
    public void Sheet_numbers_are_not_read_as_elevations(string file)
    {
        Assert.Null(StoreyNames.Parse(file).ElevationMm);
    }

    [Theory]
    [InlineData("KC-10 MAT CAT A-A.dwg")]
    [InlineData("Chi tiết cột C1.dwg")]
    [InlineData("BO TRI THEP SAN TANG 2.dwg")]
    public void Sections_details_and_rebar_layouts_are_not_plans(string file)
    {
        Assert.False(StoreyNames.Parse(file).LooksLikePlan);
    }

    [Fact]
    public void A_typical_floor_plan_gets_no_single_name()
    {
        var guess = StoreyNames.Parse("MB TANG DIEN HINH 3-10.dwg");

        Assert.Null(guess.Name);
        Assert.Contains(guess.Notes, note => note.Contains("điển hình"));
    }

    [Fact]
    public void The_table_lists_plans_bottom_to_top_and_reports_problems()
    {
        var (storeys, skipped, warnings) = StoreyNames.SuggestTable(
        [
            "KC-05 MB MAI.dwg",
            "KC-03 MB TANG 2 (+3.600).dwg",
            "KC-02 MB TANG 1 (+0.000).dwg",
            "KC-01 MB MONG (-1.500).dwg",
            "KC-04 MB TANG 3 (+40.200).dwg",
            "KC-04b MB TANG 3 sua.dwg",
            "KC-10 MAT CAT A-A.dwg",
        ]);

        Assert.Equal(["Móng", "Tầng 1", "Tầng 2", "Tầng 3", "Tầng 3", "Mái"], storeys.Select(storey => storey.Name));
        Assert.Single(skipped);
        Assert.Contains(warnings, warning => warning.Contains("\"Tầng 3\" có 2 file"));
        Assert.Contains(warnings, warning => warning.Contains("Tầng 2 → Tầng 3") && warning.Contains("36600"));
    }

    [Fact]
    public void Storey_heights_outside_the_usual_range_are_reported()
    {
        var warnings = StoreyNames.HeightWarnings([("Tầng 1", 0), ("Tầng 2", 3600), ("Tầng 3", 3900), ("Mái", 40000)]);

        Assert.Equal(2, warnings.Count);
        Assert.Contains("Tầng 2 → Tầng 3 là 300 mm", warnings[0]);
        Assert.Contains("Tầng 3 → Mái là 36100 mm", warnings[1]);
    }
}
