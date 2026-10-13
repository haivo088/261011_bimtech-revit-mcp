using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Protocol;

namespace Bimtech.RevitMcp.Commands.Common;

// Filter params shared by element queries:
//   categories   ["Walls", "OST_Doors", ...]  built-in names (with or without OST_) or category names
//   viewId       only elements visible in this view
//   levelId      only elements on this level
//   nameContains case-insensitive match on the element name
//   includeTypes include element types (families/types) instead of instances only
// Fast filters run inside Revit's collector; nameContains runs afterwards.
public sealed class ElementQuery
{
    private IReadOnlyList<string> _categories = [];
    private ElementId? _viewId;
    private ElementId? _levelId;
    private string? _nameContains;
    private bool _includeTypes;

    public static ElementQuery From(CommandParams parameters) => new()
    {
        _categories = parameters.GetStringList("categories", maxCount: 50, maxLength: 100),
        _viewId = parameters.Has("viewId") ? parameters.RequireElementId("viewId") : null,
        _levelId = parameters.Has("levelId") ? parameters.RequireElementId("levelId") : null,
        _nameContains = parameters.GetString("nameContains", maxLength: 200),
        _includeTypes = parameters.GetBool("includeTypes", false),
    };

    // Elements sorted by ID so paging is stable while the model is unchanged.
    public IEnumerable<Element> Apply(Document document)
    {
        var collector = _viewId is null
            ? new FilteredElementCollector(document)
            : new FilteredElementCollector(document, RequireView(document, _viewId).Id);

        if (_includeTypes) collector.WhereElementIsElementType();
        else collector.WhereElementIsNotElementType();

        if (_categories.Count > 0)
            collector.WherePasses(new ElementMulticategoryFilter(
                _categories.Select((name, index) => CategoryNames.Resolve(document, name, $"categories[{index}]")).ToList()));

        if (_levelId is not null)
        {
            if (document.GetElement(_levelId) is not Level)
                throw RpcException.InvalidParams("levelId", "is not a level. Use get_levels to find level IDs.");
            collector.WherePasses(new ElementLevelFilter(_levelId));
        }

        IEnumerable<Element> elements = collector;
        if (!string.IsNullOrEmpty(_nameContains))
            elements = elements.Where(element => SafeName(element).Contains(_nameContains, StringComparison.OrdinalIgnoreCase));
        return elements.OrderBy(element => element.Id.Value);
    }

    private static View RequireView(Document document, ElementId id)
    {
        if (document.GetElement(id) is not View view)
            throw RpcException.InvalidParams("viewId", "is not a view.");
        if (view.IsTemplate)
            throw RpcException.InvalidParams("viewId", "is a view template, which shows no elements.");
        return view;
    }

    // Some elements throw when reading Name; treat them as unnamed.
    internal static string SafeName(Element element)
    {
        try { return element.Name ?? ""; }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { return ""; }
    }
}
