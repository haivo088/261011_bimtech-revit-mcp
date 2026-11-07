using System.Globalization;
using System.Text.Json;
using Bimtech.RevitMcp.Planning;

namespace Bimtech.RevitMcp.Data;

// The wall type a group will use, decided by the command (needs Revit): an existing type, a type to create from a
// template type, or a problem that blocks the group. CompanyOnly: only a BTC-VachKC_ type may be reused at commit.
public sealed record WallTypeChoice(string? TypeId, string? TemplateTypeId, bool CompanyOnly, string Text, string? Problem)
{
    public bool CreatesType => TypeId is null && Problem is null;
}

// Turns wall/1.0 data into create_walls review items (same params as propose_walls, so CreateWallsAction, the preview
// and commit_plan work unchanged): one item per wall type; walls flagged by the extraction (slivers, odd thickness,
// overlap with a column) go to their own item that the reviewer decides. No Revit types.
public static class WallDataPlanner
{
    public const string Action = "create_walls";
    public const double CheckedConfidence = 0.9;
    public const double LookConfidence = 0.45;
    public const double UncheckedConfidence = 0.4;

    public static List<PlanItem> BuildItems(
        WallDataSet data, double baseXmm, double baseYmm, string baseLevelId, string topLevelId,
        Func<string, double, WallTypeChoice> chooseType, string? originProblem)
    {
        var src = data.Source;
        var common = new List<string>
        {
            Format(WallDataText.FromData, src.Schema, src.Drawing ?? "?", src.Src ?? "?", src.SrcHash ?? "?", src.GridFile ?? "?"),
            WallDataText.Joins,
            WallDataText.Existing,
        };
        if (originProblem is not null) common.Add(Format(DataText.OriginProblem, originProblem));

        var items = new List<PlanItem>();
        foreach (var byType in data.Walls.GroupBy(w => w.Type).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var thickness = byType.First().Thickness;
            var choice = chooseType(byType.Key, thickness);
            foreach (var look in new[] { false, true })
            {
                var group = byType.Where(w => data.NeedsLook(w) == look).ToList();
                if (group.Count == 0) continue;

                // A blocking problem comes first: the command and the review window show the first reason.
                var reasons = choice.Problem is null ? new List<string>(common) : [choice.Problem, .. common];
                reasons.Add(group.All(data.IsChecked) ? WallDataText.Checked : WallDataText.NotChecked);
                reasons.Add(choice.Text);
                if (choice.CreatesType) reasons.Add(WallDataText.NewType);
                var openings = group.SelectMany(w => w.Openings).ToList();
                if (openings.Count > 0)
                    reasons.Add(Format(WallDataText.Openings, openings.Count, string.Join(", ", openings.Select(o => o.Grid).Take(8))));
                if (openings.Any(o => o.Head is null)) reasons.Add(WallDataText.NoHead);
                var flags = group.SelectMany(w => w.Flags).Distinct().Select(f => WallDataText.FlagNames.GetValueOrDefault(f, f)).ToList();
                if (flags.Count > 0) reasons.Add(Format(WallDataText.Flagged, string.Join(", ", flags)));
                reasons.Add(Format(WallDataText.At, string.Join(", ", group.Select(w => w.GridStart).Take(8))
                    + (group.Count > 8 ? "..." : "")));

                var confidence = originProblem is not null || look
                    ? UncheckedConfidence
                    : choice.CreatesType ? LookConfidence : CheckedConfidence;
                var length = group.Sum(w => Math.Abs(w.X2 - w.X1) + Math.Abs(w.Y2 - w.Y1)) / 1000;
                items.Add(new PlanItem
                {
                    ItemId = "W-" + byType.Key + (look ? "-xem" : ""),
                    Action = Action,
                    Confidence = confidence,
                    Tier = choice.Problem is null ? PlanTier.Review : PlanTier.Blocked,
                    Params = JsonSerializer.SerializeToElement(new
                    {
                        summary = Format(look ? WallDataText.SummaryLook : WallDataText.Summary, byType.Key, group.Count, length, choice.Text),
                        groupKey = byType.Key,
                        thicknessMm = thickness,
                        tagName = (string?)null,
                        typeId = choice.TypeId,
                        templateTypeId = choice.TypeId is null ? choice.TemplateTypeId : null,
                        companyTypeOnly = choice.CompanyOnly,
                        baseLevelId,
                        topLevelId,
                        baseOffsetMm = 0.0,
                        topOffsetMm = 0.0,
                        tagParameterName = (string?)null,
                        // Centrelines in model coordinates (mm): x0, y0, x1, y1.
                        lines = group.Select(w => new[]
                        {
                            Math.Round(baseXmm + w.X1, 2), Math.Round(baseYmm + w.Y1, 2),
                            Math.Round(baseXmm + w.X2, 2), Math.Round(baseYmm + w.Y2, 2),
                        }).ToList(),
                        // Door openings of these walls (model coordinates, mm): x0, y0, x1, y1, sill, head.
                        openings = group.SelectMany(w => w.Openings).Where(o => o.Head is not null).Select(o => new[]
                        {
                            Math.Round(baseXmm + o.X1, 2), Math.Round(baseYmm + o.Y1, 2),
                            Math.Round(baseXmm + o.X2, 2), Math.Round(baseYmm + o.Y2, 2), o.Sill, o.Head!.Value,
                        }).ToList(),
                    }),
                    Reasons = reasons,
                    Source = JsonSerializer.SerializeToElement(new
                    {
                        name = byType.Key,
                        category = "Walls",
                        count = group.Count,
                        file = src.Drawing,
                        outlines = group.Select(w => w.Outline).Distinct().ToList(),
                    }),
                });
            }
        }
        return items;
    }

    private static string Format(string template, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, template, args);
}
