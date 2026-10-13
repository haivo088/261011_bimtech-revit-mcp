using Autodesk.Revit.DB;

namespace Bimtech.RevitMcp.Commands.Common;

// Revit stores lengths in feet internally. Every command reports fixed units instead,
// independent of the project's display settings: mm, m², m³, degrees.
public static class UnitConvert
{
    public const string Millimeters = "mm";
    public const string SquareMeters = "m2";
    public const string CubicMeters = "m3";
    public const string Degrees = "deg";

    public static double Length(double internalFeet) => Convert(internalFeet, UnitTypeId.Millimeters);

    public static object Point(XYZ point) => new { x = Length(point.X), y = Length(point.Y), z = Length(point.Z) };

    // Returns null for unit when the spec has no fixed output unit; value then stays in Revit's internal unit.
    public static (double Value, string? Unit) FromInternal(double value, ForgeTypeId spec)
    {
        if (spec.Equals(SpecTypeId.Length)) return (Convert(value, UnitTypeId.Millimeters), Millimeters);
        if (spec.Equals(SpecTypeId.Area)) return (Convert(value, UnitTypeId.SquareMeters), SquareMeters);
        if (spec.Equals(SpecTypeId.Volume)) return (Convert(value, UnitTypeId.CubicMeters), CubicMeters);
        if (spec.Equals(SpecTypeId.Angle)) return (Convert(value, UnitTypeId.Degrees), Degrees);
        return (Math.Round(value, 6), null);
    }

    private static double Convert(double value, ForgeTypeId unit) =>
        Math.Round(UnitUtils.ConvertFromInternalUnits(value, unit), 3);
}
