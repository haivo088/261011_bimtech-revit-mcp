using System.Globalization;
using System.Text.Json;
using Bimtech.RevitMcp.Planning;

namespace Bimtech.RevitMcp.Data;

// The floor type a slab will use, decided by the command (needs Revit): an existing type, the company type
// BTC_SanKC_<t> to create at commit, or a problem that blocks the slab.
public sealed record SlabTypeChoice(string? TypeId, string Text, string? Problem)
{
    public bool CreatesType => TypeId is null && Problem is null;
}

// Turns slab/1.0 data into create_floor review items (same params as propose_floors, plus holes and thicknessMm, so
// CreateFloorAction, the preview and commit_plan work): one item per slab, its openings cut as holes. Openings touching
// the slab edge cannot be cut as holes and are only reported. No Revit types.
public static class SlabDataPlanner
{
    public const string Action = "create_floor";
    public const double CheckedConfidence = 0.9;
    // Below PlanConfidence.AutoApproveFrom: the reviewer decides.
    public const double LookConfidence = 0.45;
    public const double UncheckedConfidence = 0.4;
    public const string MainZone = "main";
    private const int MaxListed = 8;

    public static List<PlanItem> BuildItems(
        SlabDataSet data, double baseXmm, double baseYmm, string levelId, string levelName,
        Func<double, SlabTypeChoice> chooseType, string? originProblem)
    {
        var src = data.Source;
        var common = new List<string>
        {
            Format(SlabDataText.FromData, src.Schema, src.Drawing ?? "?", src.Src ?? "?", src.SrcHash ?? "?", src.GridFile ?? "?"),
            SlabDataText.Outline,
            SlabDataText.Existing,
        };
        if (originProblem is not null) common.Add(Format(DataText.OriginProblem, originProblem));

        List<double[]> ToModel(IEnumerable<(double X, double Y)> points) =>
            points.Select(p => new[] { Math.Round(baseXmm + p.X, 2), Math.Round(baseYmm + p.Y, 2) }).ToList();

        var items = new List<PlanItem>();
        foreach (var slab in data.Slabs)
        {
            var choice = chooseType(slab.Thickness);
            var openings = data.OpeningsOf(slab);
            var cut = openings.Where(o => o.CanCut).ToList();
            var notCut = openings.Where(o => !o.CanCut).ToList();
            // slab/1.1 gives the inner loops (openings and zone slabs inside); slab/1.0 only the openings.
            var holes = slab.Holes ?? cut.Select(o => o.Points).ToList();

            var reasons = choice.Problem is null ? new List<string>(common) : [choice.Problem, .. common];
            reasons.Add(Format(SlabDataText.Top, levelName, slab.TopOffset));
            reasons.Add(slab.Zone == MainZone ? SlabDataText.MainZone : Format(SlabDataText.Zone, slab.Zone));
            reasons.Add(choice.Text);
            if (choice.CreatesType) reasons.Add(SlabDataText.NewType);
            if (slab.Holes is not null && holes.Count > 0)
                reasons.Add(Format(SlabDataText.InnerLoops, holes.Count));
            if (cut.Count > 0)
                reasons.Add(Format(SlabDataText.Holes, cut.Count, string.Join(", ", cut.GroupBy(o => o.Kind)
                    .Select(g => $"{SlabDataText.KindNames.GetValueOrDefault(g.Key, g.Key)} ×{g.Count()}"))));
            if (notCut.Count > 0)
                reasons.Add(Format(SlabDataText.NotCut, notCut.Count, List(notCut.Select(o => $"{o.Id} {o.GridRef}"))));

            var confidence = originProblem is not null || notCut.Count > 0
                ? UncheckedConfidence
                : choice.CreatesType ? LookConfidence : CheckedConfidence;
            var area = Area(slab.Points) / 1e6;
            items.Add(new PlanItem
            {
                ItemId = "F-" + slab.Id,
                Action = Action,
                Confidence = confidence,
                Tier = choice.Problem is null ? PlanTier.Review : PlanTier.Blocked,
                Params = JsonSerializer.SerializeToElement(new
                {
                    summary = Format(SlabDataText.Summary, slab.Id, slab.Zone, slab.Thickness, area, holes.Count, choice.Text),
                    levelId,
                    offsetMm = slab.TopOffset,
                    floorTypeId = choice.TypeId,
                    thicknessMm = slab.Thickness,
                    // Outline and holes in model coordinates (mm).
                    points = ToModel(slab.Points),
                    holes = holes.Select(ToModel).ToList(),
                    // Written to Comments: the drawing gives no use for the zones, the reviewer changes the type.
                    comment = slab.Zone == MainZone ? null : Format(SlabDataText.Comment, slab.Zone),
                }),
                Reasons = reasons,
                Source = JsonSerializer.SerializeToElement(new
                {
                    name = slab.Id,
                    category = "Floors",
                    zone = slab.Zone,
                    file = src.Drawing,
                    openings = cut.Select(o => o.Id).ToList(),
                    notCut = notCut.Select(o => o.Id).ToList(),
                }),
            });
        }
        return items;
    }

    private static double Area(IReadOnlyList<(double X, double Y)> p) =>
        Math.Abs(Enumerable.Range(0, p.Count).Sum(i => p[i].X * p[(i + 1) % p.Count].Y - p[(i + 1) % p.Count].X * p[i].Y)) / 2;

    private static string List(IEnumerable<string> names)
    {
        var list = names.ToList();
        return string.Join(", ", list.Take(MaxListed)) + (list.Count > MaxListed ? "..." : "");
    }

    private static string Format(string template, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, template, args);
}
