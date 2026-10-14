using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Protocol;

namespace Bimtech.RevitMcp.Commands.Common;

public static class CategoryNames
{
    // Accepts "OST_Walls", "Walls" (built-in name without prefix) or the category name shown in
    // this Revit's language, e.g. "Tường". Built-in names work in every language.
    public static ElementId Resolve(Document document, string text, string paramName)
    {
        var trimmed = text.Trim();
        if (!trimmed.All(char.IsDigit))
        {
            var builtInName = trimmed.StartsWith("OST_", StringComparison.OrdinalIgnoreCase) ? trimmed : "OST_" + trimmed;
            if (Enum.TryParse<BuiltInCategory>(builtInName, ignoreCase: true, out var builtIn)
                && builtIn != BuiltInCategory.INVALID)
                return new ElementId(builtIn);
        }

        foreach (Category category in document.Settings.Categories)
        {
            if (string.Equals(category.Name, trimmed, StringComparison.OrdinalIgnoreCase)) return category.Id;
        }
        throw RpcException.InvalidParams(paramName,
            $"unknown category '{text}'. Use a built-in name such as \"OST_Walls\", or call list_categories.");
    }

    // Values accepted by the "categoryType" param.
    private static readonly CategoryType[] UserFacingTypes =
        [CategoryType.Model, CategoryType.Annotation, CategoryType.AnalyticalModel];

    // Reads "categoryType": "Model", "Annotation" or "AnalyticalModel"; null when absent.
    public static CategoryType? ReadCategoryType(CommandParams parameters)
    {
        if (!parameters.Has("categoryType")) return null;
        var type = parameters.GetEnum("categoryType", CategoryType.Invalid);
        if (!UserFacingTypes.Contains(type))
            throw RpcException.InvalidParams("categoryType",
                $"must be one of {string.Join(", ", UserFacingTypes)}.");
        return type;
    }

    // Top-level categories of this type listed in Revit's Object Styles. Internal categories
    // such as Material Assets or Legend Components are not listed there, so they are excluded.
    public static HashSet<ElementId> IdsOfType(Document document, CategoryType type)
    {
        var ids = new HashSet<ElementId>();
        foreach (Category category in document.Settings.Categories)
        {
            if (category.CategoryType == type) ids.Add(category.Id);
        }
        return ids;
    }

    public static HashSet<ElementId> ListedIds(Document document)
    {
        var ids = new HashSet<ElementId>();
        foreach (Category category in document.Settings.Categories) ids.Add(category.Id);
        return ids;
    }

    // The built-in name ("OST_Walls") or null for user-defined categories.
    public static string? BuiltInName(Category? category)
    {
        if (category is null) return null;
        var builtIn = category.BuiltInCategory;
        return builtIn == BuiltInCategory.INVALID ? null : builtIn.ToString();
    }
}
