namespace Bimtech.RevitMcp.Commands.Elements;

// Sorts the elements of propose_change_type into one group per current type, the ones already of the target type,
// and the ones refused (no type, other category, type Revit does not allow for them). Revit-free: the command
// describes each element as an Element, so the rule is unit tested.
public static class TypeChangeGroups
{
    // CategoryId/TypeId are null when the element has none. TargetAllowed: the target is among the element's valid
    // types (Element.GetValidTypes).
    public sealed record Element(string Id, string? CategoryId, string? CategoryName, string? TypeId, string? TypeName, bool TargetAllowed);

    public sealed record Group(string TypeId, string TypeName, string? CategoryName, List<string> ElementIds);

    public sealed record Refusal(string ElementId, string Reason);

    public sealed record Result(List<Group> Groups, List<string> AlreadyOfType, List<Refusal> Refused);

    public static Result Sort(IEnumerable<Element> elements, string targetTypeId, string? targetCategoryId, string? targetCategoryName)
    {
        var groups = new List<Group>();
        var already = new List<string>();
        var refused = new List<Refusal>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in elements)
        {
            if (!seen.Add(element.Id)) continue;
            if (element.TypeId is null)
                refused.Add(new(element.Id, "Phần tử không có Type để đổi."));
            else if (element.CategoryId != targetCategoryId)
                refused.Add(new(element.Id, $"Category {element.CategoryName ?? "(không có)"} khác category của Type đích ({targetCategoryName})."));
            else if (element.TypeId == targetTypeId)
                already.Add(element.Id);
            else if (!element.TargetAllowed)
                refused.Add(new(element.Id, "Revit không cho đổi phần tử này sang Type đích."));
            else if (groups.Find(group => group.TypeId == element.TypeId) is { } group)
                group.ElementIds.Add(element.Id);
            else
                groups.Add(new Group(element.TypeId, element.TypeName ?? element.TypeId, element.CategoryName, [element.Id]));
        }
        return new Result(groups, already, refused);
    }

    // Review row text after the category: "CW 102-85-100p → BTC-VachKC_300 (14)".
    public static string Label(string fromTypeName, string toTypeName, int count) => $"{fromTypeName} → {toTypeName} ({count})";
}
