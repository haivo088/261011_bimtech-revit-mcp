using System.Globalization;
using System.Text.Json;
using Bimtech.RevitMcp.Planning;

namespace Bimtech.RevitMcp.Data;

// Turns grid/1.0 data into create_grid review items (same params as propose_grids, so CreateGridAction, the review
// window preview and commit_plan work unchanged). No Revit types: the command passes the base point and the grid
// names already in the model.
public static class GridDataPlanner
{
    public const string Action = "create_grid";
    // Data already checked outside Revit (dimensions, name sequence, unit, self check): sure enough to start approved.
    public const double CheckedConfidence = 0.9;
    // Data whose self check failed or is missing: the reviewer decides every grid.
    public const double UncheckedConfidence = 0.4;

    public static List<PlanItem> BuildItems(
        GridDataSet data, double baseXmm, double baseYmm, IReadOnlySet<string> existingNames, bool pin)
    {
        var src = data.Source;
        var fromData = Format(GridDataText.FromData, src.Schema, src.Src ?? "?", src.SrcHash ?? "?");
        var checkReason = data.SelfCheck switch
        {
            true => null,
            false => GridDataText.SelfCheckFailed,
            null => GridDataText.NoSelfCheck,
        };
        var confidence = data.SelfCheck == true ? CheckedConfidence : UncheckedConfidence;

        var counts = data.Grids.Where(g => g.IsNamed)
            .GroupBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var items = new List<PlanItem>();
        foreach (var grid in data.Grids)
        {
            var line = GridDataLayout.ToModelLine(grid, baseXmm, baseYmm);
            var duplicate = grid.IsNamed && counts[grid.Name] > 1;
            var inModel = grid.IsNamed && existingNames.Contains(grid.Name);
            var blocked = !grid.IsNamed || duplicate || inModel;

            var reasons = new List<string> { fromData };
            if (checkReason is not null) reasons.Add(checkReason);
            if (!grid.IsNamed) reasons.Add(GridDataText.Unnamed);
            if (inModel) reasons.Add(Format(GridDataText.NameInModel, grid.Name));
            if (duplicate) reasons.Add(Format(GridDataText.NameTwice, grid.Name));
            if (grid.Bubble == "fix") reasons.Add(Format(GridDataText.NamedByFix, src.FixFile ?? "_fix.json"));
            reasons.Add(Format(GridDataText.CadHandle, grid.Handle ?? "?", grid.BubbleHandle ?? "-"));

            var tag = grid.Kind == "aux" ? GridDataText.SummaryAux : grid.Bubble == "fix" ? GridDataText.SummaryFix : "";
            items.Add(new PlanItem
            {
                ItemId = "G-" + grid.Name + (duplicate || !grid.IsNamed ? "@" + Mm(grid.PosMm) : ""),
                Action = Action,
                Confidence = confidence,
                Tier = blocked ? PlanTier.Blocked : PlanTier.Review,
                Params = JsonSerializer.SerializeToElement(new
                {
                    summary = Format(GridDataText.Summary, grid.Name,
                        grid.IsVertical ? GridDataText.Vertical : GridDataText.Horizontal,
                        grid.IsVertical ? "x" : "y", Mm(grid.PosMm), tag),
                    name = grid.Name,
                    startMm = new { x = Math.Round(line.X0, 2), y = Math.Round(line.Y0, 2) },
                    endMm = new { x = Math.Round(line.X1, 2), y = Math.Round(line.Y1, 2) },
                    direction = grid.IsVertical ? "vertical" : "horizontal",
                    pin,
                    comments = GridDataLayout.TraceText(src, grid),
                }),
                Reasons = reasons,
                Source = JsonSerializer.SerializeToElement(new
                {
                    name = grid.Name,
                    category = "Grids",
                    file = src.Src,
                    handle = grid.Handle,
                }),
            });
        }
        return items;
    }

    private static string Format(string template, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, template, args);

    private static string Mm(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
