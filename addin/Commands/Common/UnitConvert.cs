using Autodesk.Revit.DB;

namespace Bimtech.RevitMcp.Commands.Common;

// Revit stores values in internal units (feet, pounds-force...). Every command reports
// fixed SI units instead, independent of the project's display settings.
public static class UnitConvert
{
    public const string Millimeters = "mm";

    // Spec → output unit. To support another spec, add one line.
    private static readonly (ForgeTypeId Spec, ForgeTypeId Unit, string Label)[] Table =
    [
        (SpecTypeId.Length, UnitTypeId.Millimeters, "mm"),
        (SpecTypeId.Area, UnitTypeId.SquareMeters, "m2"),
        (SpecTypeId.Volume, UnitTypeId.CubicMeters, "m3"),
        (SpecTypeId.Angle, UnitTypeId.Degrees, "deg"),
        (SpecTypeId.SectionDimension, UnitTypeId.Millimeters, "mm"),
        (SpecTypeId.BarDiameter, UnitTypeId.Millimeters, "mm"),
        (SpecTypeId.ReinforcementLength, UnitTypeId.Millimeters, "mm"),
        (SpecTypeId.ReinforcementSpacing, UnitTypeId.Millimeters, "mm"),
        (SpecTypeId.ReinforcementCover, UnitTypeId.Millimeters, "mm"),
        (SpecTypeId.ReinforcementArea, UnitTypeId.SquareMillimeters, "mm2"),
        (SpecTypeId.Mass, UnitTypeId.Kilograms, "kg"),
        (SpecTypeId.MassDensity, UnitTypeId.KilogramsPerCubicMeter, "kg/m3"),
        (SpecTypeId.UnitWeight, UnitTypeId.KilonewtonsPerCubicMeter, "kN/m3"),
        (SpecTypeId.Stress, UnitTypeId.Megapascals, "MPa"),
        (SpecTypeId.Force, UnitTypeId.Kilonewtons, "kN"),
        (SpecTypeId.LinearForce, UnitTypeId.KilonewtonsPerMeter, "kN/m"),
        (SpecTypeId.AreaForce, UnitTypeId.KilonewtonsPerSquareMeter, "kN/m2"),
        (SpecTypeId.Moment, UnitTypeId.KilonewtonMeters, "kNm"),
    ];

    public static double Length(double internalFeet) => Convert(internalFeet, UnitTypeId.Millimeters);

    public static object Point(XYZ point) => new { x = Length(point.X), y = Length(point.Y), z = Length(point.Z) };

    // Returns null for unit when the spec has no entry; value then stays in Revit's internal unit
    // and callers should rely on the parameter's display text.
    public static (double Value, string? Unit) FromInternal(double value, ForgeTypeId spec)
    {
        foreach (var (tableSpec, unit, label) in Table)
        {
            if (spec.Equals(tableSpec)) return (Convert(value, unit), label);
        }
        return (Math.Round(value, 6), null);
    }

    private static double Convert(double value, ForgeTypeId unit) =>
        Math.Round(UnitUtils.ConvertFromInternalUnits(value, unit), 3);
}
