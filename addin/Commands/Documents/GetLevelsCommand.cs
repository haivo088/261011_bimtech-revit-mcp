using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands.Common;

namespace Bimtech.RevitMcp.Commands.Documents;

public sealed class GetLevelsCommand : IRevitCommand
{
    public string Method => "get_levels";

    public object? Execute(CommandContext context)
    {
        var document = context.RequireDocument();
        return new
        {
            unit = UnitConvert.Millimeters,
            levels = new FilteredElementCollector(document)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(level => level.Elevation)
                .Select(level => new
                {
                    id = RevitJson.Id(level.Id),
                    name = level.Name,
                    elevation = UnitConvert.Length(level.Elevation),
                })
                .ToList(),
        };
    }
}
