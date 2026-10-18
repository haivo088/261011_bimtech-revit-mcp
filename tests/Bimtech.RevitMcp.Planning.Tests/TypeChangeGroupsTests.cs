using Bimtech.RevitMcp.Commands.Elements;
using Xunit;
using Element = Bimtech.RevitMcp.Commands.Elements.TypeChangeGroups.Element;

namespace Bimtech.RevitMcp.Planning.Tests;

public sealed class TypeChangeGroupsTests
{
    private const string Walls = "-2000011";
    private const string Columns = "-2001330";
    private const string Target = "900";

    private static Element Wall(string id, string typeId, string typeName, bool allowed = true) =>
        new(id, Walls, "Walls", typeId, typeName, allowed);

    [Fact]
    public void One_group_per_current_type_in_first_seen_order()
    {
        var result = TypeChangeGroups.Sort(
            [Wall("1", "10", "CW 102-85-100p"), Wall("2", "20", "Generic - 200mm"), Wall("3", "10", "CW 102-85-100p")],
            Target, Walls, "Walls");

        Assert.Equal(["10", "20"], result.Groups.Select(group => group.TypeId));
        Assert.Equal(["1", "3"], result.Groups[0].ElementIds);
        Assert.Equal("CW 102-85-100p", result.Groups[0].TypeName);
        Assert.Equal("Walls", result.Groups[0].CategoryName);
        Assert.Empty(result.Refused);
    }

    [Fact]
    public void Elements_already_of_the_target_type_are_skipped()
    {
        var result = TypeChangeGroups.Sort([Wall("1", Target, "BTC-VachKC_300"), Wall("2", "10", "CW")], Target, Walls, "Walls");

        Assert.Equal(["1"], result.AlreadyOfType);
        Assert.Single(result.Groups);
    }

    [Fact]
    public void Elements_of_another_category_or_without_type_are_refused()
    {
        var result = TypeChangeGroups.Sort(
        [
            new Element("1", Columns, "Structural Columns", "30", "C 300x300", true),
            new Element("2", null, null, null, null, false),
            Wall("3", "10", "CW"),
        ], Target, Walls, "Walls");

        Assert.Equal(["1", "2"], result.Refused.Select(refusal => refusal.ElementId));
        Assert.Contains("Structural Columns", result.Refused[0].Reason);
        Assert.Contains("Walls", result.Refused[0].Reason);
        Assert.Equal(["3"], result.Groups.Single().ElementIds);
    }

    [Fact]
    public void Elements_Revit_does_not_allow_on_the_target_are_refused()
    {
        var result = TypeChangeGroups.Sort([Wall("1", "10", "Curtain Wall 1", allowed: false)], Target, Walls, "Walls");

        Assert.Empty(result.Groups);
        Assert.Single(result.Refused);
    }

    [Fact]
    public void Repeated_ids_count_once()
    {
        var result = TypeChangeGroups.Sort([Wall("1", "10", "CW"), Wall("1", "10", "CW")], Target, Walls, "Walls");

        Assert.Equal(["1"], result.Groups.Single().ElementIds);
    }

    [Fact]
    public void Label_reads_from_to_and_count()
    {
        Assert.Equal("CW 102-85-100p → BTC-VachKC_300 (14)", TypeChangeGroups.Label("CW 102-85-100p", "BTC-VachKC_300", 14));
    }
}
