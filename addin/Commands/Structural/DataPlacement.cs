using System.Globalization;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands.Common;
using Bimtech.RevitMcp.Data;
using Bimtech.RevitMcp.Protocol;

namespace Bimtech.RevitMcp.Commands.Structural;

// Placement rules shared by the propose_*_from_data commands: levels from the data and the grid origin check.
internal static class DataPlacement
{
    private const double OriginToleranceMm = 1.0;

    // Base: baseLevelId, else the level named levelBase. Top: topLevelId, else "next" (next level up) or the level named levelTop.
    public static (Level Base, Level Top) ResolveLevels(Document document, CommandParams p, string? levelBase, string? levelTop)
    {
        var levels = new FilteredElementCollector(document).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
        Level ByName(string name, string param) => levels.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw RpcException.InvalidParams(param, $"the model has no level named '{name}'. Pass {param}.");

        var baseLevel = p.Has("baseLevelId")
            ? StructuralJson.RequireLevel(document, p.RequireElementId("baseLevelId"), "baseLevelId")
            : levelBase is { } name ? ByName(name, "baseLevelId")
            : throw RpcException.InvalidParams("baseLevelId", "the data has no meta.level.base. Pass baseLevelId.");
        Level topLevel;
        if (p.Has("topLevelId"))
            topLevel = StructuralJson.RequireLevel(document, p.RequireElementId("topLevelId"), "topLevelId");
        else if (levelTop is null or "next")
            topLevel = levels.FirstOrDefault(l => l.Elevation > baseLevel.Elevation + 1e-6)
                ?? throw RpcException.InvalidParams("topLevelId", $"no level above {baseLevel.Name}. Pass topLevelId.");
        else
            topLevel = ByName(levelTop, "topLevelId");
        if (topLevel.Elevation <= baseLevel.Elevation)
            throw RpcException.InvalidParams("topLevelId", "must be higher than the base level.");
        return (baseLevel, topLevel);
    }

    // A single level (beams): the level given in param, else the level named levelName.
    public static Level ResolveBaseLevel(Document document, CommandParams p, string param, string? levelName)
    {
        if (p.Has(param)) return StructuralJson.RequireLevel(document, p.RequireElementId(param), param);
        if (levelName is null) throw RpcException.InvalidParams(param, $"the data has no meta.level.base. Pass {param}.");
        return new FilteredElementCollector(document).OfClass(typeof(Level)).Cast<Level>()
                .FirstOrDefault(l => string.Equals(l.Name, levelName, StringComparison.OrdinalIgnoreCase))
            ?? throw RpcException.InvalidParams(param, $"the model has no level named '{levelName}'. Pass {param}.");
    }

    // The data's origin grids (e.g. A and 1 of "A-1") must pass through the Project Base Point; else positions shift.
    // Returns the problem text, or null when the origin is right (or the data names none).
    public static string? CheckOrigin(Document document, string? origin, XYZ basePoint)
    {
        if (origin is null || origin.Split('-', 2) is not [var yName, var xName]) return null;
        var grids = new FilteredElementCollector(document).OfClass(typeof(Grid)).Cast<Grid>().ToList();
        foreach (var name in new[] { yName, xName })
        {
            var grid = grids.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
            if (grid is null) return Format(DataText.OriginMissing, name);
            if (grid.Curve is not Line line) continue;   // curved grids: not checked
            var flat = new XYZ(basePoint.X, basePoint.Y, line.Origin.Z);
            var off = UnitConvert.Length(Line.CreateUnbound(line.Origin, line.Direction).Project(flat).Distance);
            if (off > OriginToleranceMm)
                return Format(DataText.OriginOff, name, Math.Round(off));
        }
        return null;
    }

    private static string Format(string template, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, template, args);
}
