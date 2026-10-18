using System.Text.Json;

namespace Bimtech.RevitMcp.Planning;

// What the person already decided in earlier plans, so the next storey does not ask the same question again.
// Read from the plan files only; no Revit types, so it is tested without Revit.
public static class PlanHistory
{
    // A column the person rejected: its centre and sides in model mm.
    public sealed record Spot(double X, double Y, double SizeAMm, double SizeBMm);

    public sealed record ColumnDecisions(string PlanId, string Title, IReadOnlyList<Spot> Rejected);

    // Centres this close and sides this close are the same spot one storey up: grid snapping moves a centre a little.
    public const double SameCentreMm = 100;
    public const double SameSizeMm = 20;

    // What the person decided for the storey below (column plans whose top level is this base level), oldest first so
    // later decisions win: a spot rejected in one plan and built in a later one (a repair) is no longer rejected.
    // Null when no column plan of the storey below was committed.
    public static ColumnDecisions? ColumnsBelow(IEnumerable<Plan> plans, string baseLevelId)
    {
        var below = plans
            .Where(plan => plan.Kind == "create_columns" && plan.Status == PlanStatus.Committed && TopLevelId(plan) == baseLevelId)
            .OrderBy(plan => plan.CreatedAt)
            .ToList();
        if (below.Count == 0) return null;
        var rejected = new List<Spot>();
        foreach (var item in below.SelectMany(plan => plan.Items))
        {
            var spots = Columns(item.Params).ToList();
            if (item.Status == PlanItemStatus.Rejected) rejected.AddRange(spots);
            else if (item.Status == PlanItemStatus.Applied)
                rejected.RemoveAll(spot => spots.Any(built => Matches(spot, built.X, built.Y, built.SizeAMm, built.SizeBMm)));
        }
        return new ColumnDecisions(below[^1].PlanId, below[^1].Title, rejected);
    }

    public static bool Matches(Spot spot, double x, double y, double lengthMm, double widthMm) =>
        Math.Sqrt(Math.Pow(spot.X - x, 2) + Math.Pow(spot.Y - y, 2)) <= SameCentreMm
        && Math.Abs(spot.SizeAMm - Math.Min(lengthMm, widthMm)) <= SameSizeMm
        && Math.Abs(spot.SizeBMm - Math.Max(lengthMm, widthMm)) <= SameSizeMm;

    // scope.topLevel.id of a create_columns plan.
    private static string? TopLevelId(Plan plan) =>
        plan.Scope is { ValueKind: JsonValueKind.Object } scope
            && scope.TryGetProperty("topLevel", out var top) && top.ValueKind == JsonValueKind.Object
            && top.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;

    // params.columns of a create_columns item: [{ x, y, lengthMm, widthMm, ... }].
    private static IEnumerable<Spot> Columns(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("columns", out var columns) || columns.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var column in columns.EnumerateArray())
        {
            if (Number(column, "x") is not { } x || Number(column, "y") is not { } y
                || Number(column, "lengthMm") is not { } length || Number(column, "widthMm") is not { } width)
                continue;
            yield return new Spot(x, y, Math.Min(length, width), Math.Max(length, width));
        }
    }

    private static double? Number(JsonElement json, string name) =>
        json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
}
