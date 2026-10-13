using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands.Common;

namespace Bimtech.RevitMcp.Commands.Documents;

// Counts element instances per category, so Claude can discover valid "categories" filter values.
// "categoryType" (optional) keeps only listed categories of that type, like get_elements.
public sealed class ListCategoriesCommand : IRevitCommand
{
    public string Method => "list_categories";
    public CommandAccess Access => CommandAccess.Read;

    public object? Execute(CommandContext context)
    {
        var document = context.RequireDocument();
        var categoryType = CategoryNames.ReadCategoryType(context.Params);
        var listed = CategoryNames.ListedIds(document);
        var counts = new Dictionary<ElementId, (Category Category, int Count)>();
        var scanned = 0;
        foreach (var element in new FilteredElementCollector(document).WhereElementIsNotElementType())
        {
            if (++scanned % 1000 == 0) context.Cancellation.ThrowIfCancellationRequested();
            var category = element.Category;
            if (category is null) continue;
            if (categoryType is { } type && (category.CategoryType != type || !listed.Contains(category.Id))) continue;
            counts[category.Id] = counts.TryGetValue(category.Id, out var entry)
                ? (entry.Category, entry.Count + 1)
                : (category, 1);
        }

        return new
        {
            categories = counts.Values
                .OrderByDescending(entry => entry.Count)
                .ThenBy(entry => entry.Category.Name)
                .Select(entry => new
                {
                    name = entry.Category.Name,
                    builtInCategory = CategoryNames.BuiltInName(entry.Category),
                    type = entry.Category.CategoryType.ToString(),
                    // False for internal categories that Revit hides from Object Styles.
                    listed = listed.Contains(entry.Category.Id),
                    count = entry.Count,
                })
                .ToList(),
        };
    }
}
