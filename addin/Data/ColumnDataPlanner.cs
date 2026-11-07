using System.Globalization;
using System.Text.Json;
using Bimtech.RevitMcp.Planning;

namespace Bimtech.RevitMcp.Data;

// The type a group of columns will use, decided by the command (needs Revit): an existing type, a type to create
// in a family, or a problem that blocks the group.
public sealed record ColumnTypeChoice(string? TypeId, string? TemplateFamilyId, string Text, string? Problem)
{
    public bool CreatesType => TypeId is null && Problem is null;
}

// Turns column/1.0 data into create_columns review items (same params as propose_columns, so CreateColumnsAction,
// the preview and commit_plan work unchanged): one item per type, planted columns in their own item, hidden columns
// not built. No Revit types: the command passes the base point, level IDs, the type choice and the existing check.
public static class ColumnDataPlanner
{
    public const string Action = "create_columns";
    public const double CheckedConfidence = 0.9;
    // Below PlanConfidence.AutoApproveFrom: the reviewer decides.
    public const double LookConfidence = 0.45;
    public const double UncheckedConfidence = 0.4;

    public sealed record Result(List<PlanItem> Items, List<DataColumn> Hidden, int SkippedExisting);

    public static Result BuildItems(
        ColumnDataSet data, double baseXmm, double baseYmm, string baseLevelId, string topLevelId,
        Func<string, double, double, ColumnTypeChoice> chooseType, Func<double, double, bool> isExisting, string? originProblem)
    {
        var src = data.Source;
        var common = new List<string>
        {
            Format(ColumnDataText.FromData, src.Schema, src.Src ?? "?", src.SrcHash ?? "?", src.GridFile ?? "?"),
            Format(data.IsChecked ? ColumnDataText.Checked : ColumnDataText.NotChecked, data.Checks.DynSizeOk, data.Columns.Count),
        };
        if (originProblem is not null) common.Add(Format(DataText.OriginProblem, originProblem));

        var hidden = data.Columns.Where(c => c.Kind == "hidden").ToList();
        var items = new List<PlanItem>();
        var skippedTotal = 0;
        foreach (var byType in data.Columns.Where(c => c.Kind != "hidden").GroupBy(c => c.Type).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var first = byType.First();
            var choice = chooseType(byType.Key, first.B, first.H);
            foreach (var planted in new[] { false, true })
            {
                var group = byType.Where(c => (c.Kind == "planted") == planted).ToList();
                if (group.Count == 0) continue;

                // A blocking problem comes first: the command and the review window show the first reason.
                var reasons = choice.Problem is null ? new List<string>(common) : [choice.Problem, .. common];
                reasons.Add(choice.Text);
                if (choice.CreatesType) reasons.Add(ColumnDataText.NewType);
                if (planted) reasons.Add(ColumnDataText.Planted);
                var unknown = group.Count(c => c.Kind is null);
                if (unknown > 0) reasons.Add(Format(ColumnDataText.UnknownKind, unknown));

                var columns = new List<object>();
                var skipped = 0;
                foreach (var c in group)
                {
                    var (x, y) = (baseXmm + c.X, baseYmm + c.Y);
                    if (isExisting(x, y))
                    {
                        skipped++;
                        continue;
                    }
                    columns.Add(new
                    {
                        x = Math.Round(x, 2),
                        y = Math.Round(y, 2),
                        // Length along X, width along Y: ColumnPlacer turns the b x h type to fit them.
                        angleDeg = 0.0,
                        lengthMm = c.SizeXmm,
                        widthMm = c.SizeYmm,
                        at = c.GridRef,
                    });
                }
                skippedTotal += skipped;
                if (skipped > 0) reasons.Add(Format(ColumnDataText.Existing, skipped));
                if (columns.Count == 0 && choice.Problem is null) continue;
                reasons.Add(Format(ColumnDataText.At, string.Join(", ", group.Select(c => c.GridRef).Take(8))
                    + (group.Count > 8 ? "..." : "")));

                var confidence = !data.IsChecked || originProblem is not null || planted || unknown > 0
                    ? UncheckedConfidence
                    : choice.CreatesType ? LookConfidence : CheckedConfidence;
                items.Add(new PlanItem
                {
                    ItemId = "K-" + byType.Key + (planted ? "-cay" : ""),
                    Action = Action,
                    Confidence = confidence,
                    Tier = choice.Problem is null ? PlanTier.Review : PlanTier.Blocked,
                    Params = JsonSerializer.SerializeToElement(new
                    {
                        summary = Format(planted ? ColumnDataText.SummaryPlanted : ColumnDataText.Summary,
                            byType.Key, columns.Count, choice.Text),
                        groupKey = byType.Key,
                        mark = (string?)null,
                        baseLevelId,
                        topLevelId,
                        typeId = choice.TypeId,
                        templateFamilyId = choice.TypeId is null ? choice.TemplateFamilyId : null,
                        sizeAMm = first.B,
                        sizeBMm = first.H,
                        columns,
                    }),
                    Reasons = reasons,
                    Source = JsonSerializer.SerializeToElement(new
                    {
                        name = byType.Key,
                        category = "Structural Columns",
                        count = columns.Count,
                        file = src.Src,
                        handles = group.Select(c => c.Handle).ToList(),
                    }),
                });
            }
        }
        return new Result(items, hidden, skippedTotal);
    }

    private static string Format(string template, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, template, args);
}
