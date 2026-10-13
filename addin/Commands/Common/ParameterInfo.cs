using Autodesk.Revit.DB;

namespace Bimtech.RevitMcp.Commands.Common;

// Converts a Revit parameter to JSON. "value" uses the fixed units in UnitConvert.cs;
// "display" is Revit's own formatted text in the project's units, for showing to people.
public static class ParameterInfo
{
    public static object From(Parameter parameter, Document document)
    {
        var spec = parameter.Definition.GetDataType();
        var (value, unit) = ReadValue(parameter, spec, document);
        return new
        {
            name = parameter.Definition.Name,
            value,
            unit,
            display = parameter.HasValue ? parameter.AsValueString() ?? parameter.AsString() : null,
            storageType = parameter.StorageType.ToString(),
            isReadOnly = parameter.IsReadOnly,
            isShared = parameter.IsShared,
            builtIn = parameter.Definition is InternalDefinition { BuiltInParameter: var builtIn }
                && builtIn != BuiltInParameter.INVALID ? builtIn.ToString() : null,
        };
    }

    // Parameters sorted by name; names compared case-insensitively when a filter is given.
    public static IEnumerable<Parameter> Select(Element element, IReadOnlyCollection<string> names)
    {
        IEnumerable<Parameter> parameters = element.Parameters.Cast<Parameter>();
        if (names.Count > 0)
        {
            var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            parameters = parameters.Where(parameter => wanted.Contains(parameter.Definition.Name));
        }
        return parameters.OrderBy(parameter => parameter.Definition.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static (object? Value, string? Unit) ReadValue(Parameter parameter, ForgeTypeId spec, Document document)
    {
        if (!parameter.HasValue) return (null, null);
        switch (parameter.StorageType)
        {
            case StorageType.String:
                return (parameter.AsString(), null);
            case StorageType.Integer:
                var integer = parameter.AsInteger();
                return spec.Equals(SpecTypeId.Boolean.YesNo) ? (integer != 0, null) : (integer, null);
            case StorageType.Double:
                var (number, unit) = UnitConvert.FromInternal(parameter.AsDouble(), spec);
                return (number, unit);
            case StorageType.ElementId:
                var id = parameter.AsElementId();
                if (id == ElementId.InvalidElementId) return (null, null);
                var target = document.GetElement(id);
                return (new { id = RevitJson.Id(id), name = target is null ? null : ElementQuery.SafeName(target) }, null);
            default:
                return (null, null);
        }
    }
}
