using System.Text.Json;
using Autodesk.Revit.DB;
using Bimtech.StructuralCore.Levels;

namespace Bimtech.RevitMcp.Planning.Actions;

// Written last by propose_levels and propose_grids: grids span every level and levels span every grid (DatumExtents).
// Reads the model at commit, so it also covers the levels and grids the earlier items of the same plan created.
//   { "gridMarginMm": 1000, "levelMarginMm": 2000 }
public sealed class FitDatumExtentsAction : IPlanAction
{
    public const string Name = "fit_datum_extents";
    public const double DefaultGridMarginMm = 1000;
    public const double DefaultLevelMarginMm = 2000;

    public string Action => Name;

    public object Apply(Document document, JsonElement parameters)
    {
        var result = DatumExtents.Fit(document,
            ActionParams.OptionalNumber(parameters, "gridMarginMm") ?? DefaultGridMarginMm,
            ActionParams.OptionalNumber(parameters, "levelMarginMm") ?? DefaultLevelMarginMm);
        if (result.Grids == 0 && result.Levels == 0)
            throw new PlanActionException(string.Join(" ", result.Problems));
        return new
        {
            grids = result.Grids,
            levels = result.Levels,
            gridRangeMm = new[] { result.BottomMm, result.TopMm },
            views = result.Views,
            problems = result.Problems.Take(20),
            problemCount = result.Problems.Count,
        };
    }
}
