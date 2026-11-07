using System.Globalization;
using System.Text.Json;
using Bimtech.RevitMcp.Planning;

namespace Bimtech.RevitMcp.Data;

// The beam type a group will use, decided by the command (needs Revit): an existing type, a b x h type to create in a
// family, or a problem that blocks the group.
public sealed record BeamTypeChoice(string? TypeId, string? TemplateFamilyId, string Text, string? Problem)
{
    public bool CreatesType => TypeId is null && Problem is null;
}

// Turns beam/1.0 data into create_beams review items (same params as propose_beams, so CreateBeamsAction, the preview
// and commit_plan work unchanged): one item per b x h type, each beam keeping its own name as Mark; beams flagged by
// the extraction go to their own item (-xem) that the reviewer decides. Beams without size and strips that are not
// beams are not built. No Revit types.
public static class BeamDataPlanner
{
    public const string Action = "create_beams";
    public const double CheckedConfidence = 0.9;
    // Below PlanConfidence.AutoApproveFrom: the reviewer decides.
    public const double LookConfidence = 0.45;
    public const double UncheckedConfidence = 0.4;
    private const int MaxListed = 8;

    public static List<PlanItem> BuildItems(
        BeamDataSet data, double baseXmm, double baseYmm, string levelId, string levelName,
        Func<string, double, double, BeamTypeChoice> chooseType, string? originProblem)
    {
        var src = data.Source;
        var offsetMm = src.TopOffsetMm ?? 0;
        var common = new List<string>
        {
            Format(BeamDataText.FromData, src.Schema, src.Drawing ?? "?", src.Src ?? "?", src.SrcHash ?? "?", src.GridFile ?? "?"),
            BeamDataText.Supports,
            BeamDataText.Existing,
            Format(BeamDataText.Top, levelName, offsetMm),
        };
        if (originProblem is not null) common.Add(Format(DataText.OriginProblem, originProblem));

        var items = new List<PlanItem>();
        foreach (var byType in data.Buildable.GroupBy(b => b.Type).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var (bMm, hMm) = (byType.First().B!.Value, byType.First().H!.Value);
            var choice = chooseType(byType.Key, bMm, hMm);
            foreach (var look in new[] { false, true })
            {
                var group = byType.Where(b => b.NeedsLook == look).ToList();
                if (group.Count == 0) continue;

                // A blocking problem comes first: the command and the review window show the first reason.
                var reasons = choice.Problem is null ? new List<string>(common) : [choice.Problem, .. common];
                reasons.Add(choice.Text);
                if (choice.CreatesType) reasons.Add(BeamDataText.NewType);
                reasons.Add(Format(BeamDataText.Names, List(group.Select(b => b.Name ?? "?").Distinct())));
                var derived = group.Where(b => b.SizeFrom is not null and not "label").ToList();
                if (derived.Count > 0)
                    reasons.Add(Format(BeamDataText.SizeDerived, derived.Count,
                        string.Join(", ", derived.Select(b => BeamDataText.SizeFromNames.GetValueOrDefault(b.SizeFrom!, b.SizeFrom!)).Distinct())));
                var flags = group.SelectMany(b => b.Flags).Distinct().Select(f => BeamDataText.FlagNames.GetValueOrDefault(f, f)).ToList();
                if (flags.Count > 0) reasons.Add(Format(BeamDataText.Flagged, string.Join(", ", flags)));
                reasons.Add(Format(BeamDataText.At, List(group.Select(b => b.GridStart))));

                var confidence = originProblem is not null || look
                    ? UncheckedConfidence
                    : choice.CreatesType ? LookConfidence : CheckedConfidence;
                var length = group.Sum(b => Math.Abs(b.X2 - b.X1) + Math.Abs(b.Y2 - b.Y1)) / 1000;
                items.Add(new PlanItem
                {
                    ItemId = "B-" + byType.Key + (look ? "-xem" : ""),
                    Action = Action,
                    Confidence = confidence,
                    Tier = choice.Problem is null ? PlanTier.Review : PlanTier.Blocked,
                    Params = JsonSerializer.SerializeToElement(new
                    {
                        summary = Format(look ? BeamDataText.SummaryLook : BeamDataText.Summary, byType.Key, group.Count, length, choice.Text),
                        levelId,
                        offsetMm,
                        typeId = choice.TypeId,
                        templateFamilyId = choice.TypeId is null ? choice.TemplateFamilyId : null,
                        sizeBMm = bMm,
                        sizeHMm = hMm,
                        mark = (string?)null,
                        // Beam axes in model coordinates (mm), centre to centre of the supports; each keeps its name as Mark.
                        beams = group.Select(b => new
                        {
                            x0 = Math.Round(baseXmm + b.X1, 2),
                            y0 = Math.Round(baseYmm + b.Y1, 2),
                            x1 = Math.Round(baseXmm + b.X2, 2),
                            y1 = Math.Round(baseYmm + b.Y2, 2),
                            mark = b.Name,
                        }).ToList(),
                    }),
                    Reasons = reasons,
                    Source = JsonSerializer.SerializeToElement(new
                    {
                        name = byType.Key,
                        category = "Structural Framing",
                        count = group.Count,
                        file = src.Drawing,
                        names = group.Select(b => b.Name).Distinct().ToList(),
                    }),
                });
            }
        }
        return items;
    }

    private static string List(IEnumerable<string> names)
    {
        var list = names.ToList();
        return string.Join(", ", list.Take(MaxListed)) + (list.Count > MaxListed ? "..." : "");
    }

    private static string Format(string template, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, template, args);
}
