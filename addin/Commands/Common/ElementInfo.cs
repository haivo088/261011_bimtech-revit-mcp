using Autodesk.Revit.DB;

namespace Bimtech.RevitMcp.Commands.Common;

// Builds the short element description used by every list command.
// Type and level names are cached because many elements share them.
public sealed class ElementInfo(Document document)
{
    private readonly Dictionary<ElementId, string?> _names = [];

    public object Summary(Element element) => new
    {
        id = RevitJson.Id(element.Id),
        name = ElementQuery.SafeName(element),
        category = element.Category?.Name,
        builtInCategory = CategoryNames.BuiltInName(element.Category),
        typeName = CachedName(element.GetTypeId()),
        level = CachedName(element.LevelId),
    };

    private string? CachedName(ElementId? id)
    {
        if (id is null || id == ElementId.InvalidElementId) return null;
        if (!_names.TryGetValue(id, out var name))
        {
            var element = document.GetElement(id);
            name = element is null ? null : ElementQuery.SafeName(element);
            _names[id] = name;
        }
        return name;
    }
}
