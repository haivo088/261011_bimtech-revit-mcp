using Bimtech.RevitMcp.Recognition;
using Xunit;
using Candidate = Bimtech.RevitMcp.Recognition.WallTypes.Candidate;

namespace Bimtech.RevitMcp.Recognition.Tests;

public sealed class WallTypesTests
{
    // The types of model 261016: the Revit template's cavity wall is 299 mm, the company wall 300 mm.
    private static readonly Candidate CavityWall = new("CW 102-85-100p", 299.0, StructureIsConcrete: false);
    private static readonly Candidate CompanyWall = new("BTC-VachKC_300", 300.0, StructureIsConcrete: true);

    [Fact]
    public void Company_wall_wins_over_the_template_cavity_wall()
    {
        var exact = WallTypes.FindExact([CavityWall, CompanyWall], type => type, 300);

        Assert.Equal(CompanyWall, exact);
    }

    [Theory]
    [InlineData("CW 102-85-100p", 300.0)]
    [InlineData("CW 102-50-215p", 300.0)]
    [InlineData("Brick - 300mm", 300.0)]
    [InlineData("Generic - 300mm", 300.0)]
    [InlineData("Concrete Masonry Units 300", 300.0)]
    [InlineData("Interior - 138mm Partition (2-hr)", 300.0)]
    [InlineData("Exterior - Render on Block", 300.0)]
    [InlineData("Vách gạch 300", 300.0)]
    public void Template_walls_are_never_exact_even_of_concrete(string name, double widthMm)
    {
        Assert.True(WallTypes.IsGeneric(name));
        Assert.False(WallTypes.IsExactMatch(new Candidate(name, widthMm, StructureIsConcrete: true), widthMm));
    }

    [Fact]
    public void Template_cavity_wall_alone_gives_no_exact_type_but_is_still_nearest()
    {
        Assert.Null(WallTypes.FindExact([CavityWall], type => type, 300));
        Assert.Equal(CavityWall, WallTypes.FindNearest([CavityWall], type => type, 300));
    }

    [Theory]
    [InlineData("Vách BTCT 300", false)]
    [InlineData("VKC bê tông 300", false)]
    [InlineData("Wall Concrete 300", false)]
    [InlineData("V300", true)]
    public void Concrete_walls_match_within_one_millimetre(string name, bool concreteLayer)
    {
        Assert.True(WallTypes.IsExactMatch(new Candidate(name, 300.8, concreteLayer), 300));
        Assert.False(WallTypes.IsExactMatch(new Candidate(name, 301.5, concreteLayer), 300));
    }

    [Fact]
    public void A_wall_neither_named_nor_layered_as_concrete_is_not_exact()
    {
        Assert.False(WallTypes.IsExactMatch(new Candidate("Wall 300", 300, StructureIsConcrete: false), 300));
    }

    [Fact]
    public void Company_wall_must_have_exactly_the_thickness()
    {
        Assert.Equal(0, WallTypes.Rank(new Candidate("BTC-VachKC_300", 300.3, false), 300));
        // Not exact as a company type, but its name still says vách: a concrete match within 1 mm.
        Assert.Equal(1, WallTypes.Rank(new Candidate("BTC-VachKC_300", 300.8, false), 300));
        Assert.Null(WallTypes.Rank(new Candidate("BTC-VachKC_250", 250, true), 300));
    }

    [Fact]
    public void Company_type_comes_first_then_closest_thickness()
    {
        var concrete = new Candidate("Vách BTCT 300", 300.6, true);
        var company = new Candidate("BTC-VachKC_300", 300.4, true);
        var closerConcrete = new Candidate("VKC 300 bê tông", 300.0, false);

        Assert.Equal(company, WallTypes.FindExact([concrete, closerConcrete, company], type => type, 300));
        Assert.Equal(closerConcrete, WallTypes.FindExact([concrete, closerConcrete], type => type, 300));
    }

    [Fact]
    public void Company_only_ignores_other_concrete_types()
    {
        var concrete = new Candidate("Vách BTCT 300", 300.0, true);

        Assert.Null(WallTypes.FindExact([concrete, CavityWall], type => type, 300, companyOnly: true));
        Assert.Equal(CompanyWall, WallTypes.FindExact([concrete, CompanyWall], type => type, 300, companyOnly: true));
    }

    [Fact]
    public void Template_for_a_new_type_prefers_a_company_wall_then_concrete()
    {
        var company = new Candidate("BTC-VachKC_200", 200, true);
        var concrete = new Candidate("Vách BTCT 250", 250, true);
        var noLayers = new Candidate("BTC-VachKC_350", 350, true, HasLayers: false);

        Assert.Equal(company, WallTypes.FindTemplate([CavityWall, concrete, company, noLayers], type => type, 350));
        Assert.Equal(concrete, WallTypes.FindTemplate([CavityWall, concrete], type => type, 300));
        Assert.Equal(CavityWall, WallTypes.FindTemplate([CavityWall], type => type, 300));
    }

    [Theory]
    [InlineData("Bê tông B25", true)]
    [InlineData("Concrete, Cast-in-Place gray", true)]
    [InlineData("BTCT M300", true)]
    [InlineData("Brick, Common", false)]
    [InlineData(null, false)]
    public void Concrete_materials_are_read_from_name_or_class(string? text, bool concrete)
    {
        Assert.Equal(concrete, WallTypes.MaterialSaysConcrete(text));
    }

    [Fact]
    public void New_type_name_uses_the_rounded_thickness()
    {
        Assert.Equal("BTC-VachKC_300", WallTypes.NewTypeName(299.8));
    }
}
