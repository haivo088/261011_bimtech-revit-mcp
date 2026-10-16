using System.Globalization;
using System.Text.Json;
using Bimtech.RevitMcp.Planning;
using Bimtech.RevitMcp.Planning.Actions;

namespace Bimtech.RevitMcp.Commands.Structural;

// The last item of propose_levels and propose_grids: grids span every level and levels span every grid
// (FitDatumExtentsAction). Proposed only when the model will have both levels and grids after the plan.
internal static class DatumExtentsItem
{
    public const string ItemId = "X1";

    public static (double GridMarginMm, double LevelMarginMm) ReadMargins(CommandParams p) => (
        p.GetDouble("gridMarginMm", FitDatumExtentsAction.DefaultGridMarginMm, 0, 50_000),
        p.GetDouble("levelMarginMm", FitDatumExtentsAction.DefaultLevelMarginMm, 0, 50_000));

    // levels: name and elevation of every level the model will have after the plan; grids: how many it will have.
    public static PlanItem? Create(
        IReadOnlyCollection<(string Name, double ElevationMm)> levels, int grids, double gridMarginMm, double levelMarginMm)
    {
        if (levels.Count == 0 || grids == 0) return null;
        var lowest = levels.MinBy(level => level.ElevationMm);
        var highest = levels.MaxBy(level => level.ElevationMm);
        return new PlanItem
        {
            ItemId = ItemId,
            Action = FitDatumExtentsAction.Name,
            Params = JsonSerializer.SerializeToElement(new
            {
                summary = $"Căn phạm vi: {grids} trục cao từ {Mm(lowest.ElevationMm - gridMarginMm)} tới "
                    + $"{Mm(highest.ElevationMm + gridMarginMm)} mm ({lowest.Name} −{Mm(gridMarginMm)}, {highest.Name} "
                    + $"+{Mm(gridMarginMm)}) · {levels.Count} level dài quá trục biên {Mm(levelMarginMm)} mm",
                gridMarginMm,
                levelMarginMm,
            }),
            Reasons =
            [
                "Chạy sau các mục khác của kế hoạch, theo model lúc ghi: kéo mọi trục và mọi level, kể cả cái có sẵn. "
                    + "Bỏ mục này nếu muốn tự kéo.",
            ],
            Source = JsonSerializer.SerializeToElement(new { name = "Grid và Level", category = "Grids" }),
        };
    }

    private static string Mm(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
