using System.Globalization;
using Autodesk.Revit.DB;

namespace Bimtech.RevitMcp.Commands;

// Shared conversions from Revit objects to JSON-friendly values.
public static class RevitJson
{
    // Element IDs are sent as strings so 64-bit values survive JavaScript numbers.
    public static string Id(ElementId id) => id.Value.ToString(CultureInfo.InvariantCulture);

    public static string? IdOrNull(ElementId? id) =>
        id is null || id == ElementId.InvalidElementId ? null : Id(id);
}
