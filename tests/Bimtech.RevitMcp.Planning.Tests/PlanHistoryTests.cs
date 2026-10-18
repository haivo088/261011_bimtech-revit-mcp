using System.Text.Json;
using Bimtech.RevitMcp.Planning;
using Xunit;

namespace Bimtech.RevitMcp.Planning.Tests;

// What the person rejected on the storey below is read back from the committed column plan.
public sealed class PlanHistoryTests
{
    private static Plan ColumnPlan(string topLevelId, PlanStatus status, DateTimeOffset createdAt, params PlanItem[] items) => new()
    {
        PlanId = "pln_20261018_" + Guid.NewGuid().ToString("N")[..6],
        Kind = "create_columns",
        Title = $"Dựng cột → {topLevelId}",
        CreatedAt = createdAt,
        Status = status,
        Scope = JsonSerializer.SerializeToElement(new { topLevel = new { id = topLevelId, name = "Tầng" } }),
        Items = [.. items],
    };

    private static PlanItem Group(PlanItemStatus status, params (double X, double Y, double Length, double Width)[] columns) => new()
    {
        ItemId = "K-" + Guid.NewGuid().ToString("N")[..4],
        Action = "create_columns",
        Status = status,
        Params = JsonSerializer.SerializeToElement(new
        {
            columns = columns.Select(c => new { x = c.X, y = c.Y, angleDeg = 0, lengthMm = c.Length, widthMm = c.Width, at = (string?)null }),
        }),
    };

    [Fact]
    public void Rejected_columns_of_the_committed_plans_below_are_read()
    {
        var day = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
        var older = ColumnPlan("354118", PlanStatus.Committed, day, Group(PlanItemStatus.Rejected, (0, 0, 150, 150)));
        var latest = ColumnPlan("354118", PlanStatus.Committed, day.AddHours(1),
            Group(PlanItemStatus.Applied, (6000, 0, 1400, 350)), Group(PlanItemStatus.Rejected, (100, 200, 1100, 250), (300, 200, 150, 150)));
        var notCommitted = ColumnPlan("354118", PlanStatus.InReview, day.AddHours(2), Group(PlanItemStatus.Rejected, (9, 9, 150, 150)));
        var otherStorey = ColumnPlan("354044", PlanStatus.Committed, day.AddHours(3), Group(PlanItemStatus.Rejected, (9, 9, 150, 150)));

        var below = PlanHistory.ColumnsBelow([latest, older, notCommitted, otherStorey], "354118");

        Assert.NotNull(below);
        Assert.Equal(latest.PlanId, below.PlanId);
        Assert.Equal([new PlanHistory.Spot(0, 0, 150, 150), new PlanHistory.Spot(100, 200, 250, 1100), new PlanHistory.Spot(300, 200, 150, 150)],
            below.Rejected);
    }

    [Fact]
    public void A_spot_built_by_a_later_plan_is_no_longer_rejected()
    {
        var day = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
        var first = ColumnPlan("354118", PlanStatus.Committed, day,
            Group(PlanItemStatus.Rejected, (47375, 700, 350, 1400)), Group(PlanItemStatus.Rejected, (0, 0, 150, 150)));
        var repair = ColumnPlan("354118", PlanStatus.Committed, day.AddHours(1), Group(PlanItemStatus.Applied, (47380, 700, 1400, 350)));

        var below = PlanHistory.ColumnsBelow([repair, first], "354118");

        Assert.Equal([new PlanHistory.Spot(0, 0, 150, 150)], below!.Rejected);
    }

    [Fact]
    public void Nothing_below_gives_null()
    {
        Assert.Null(PlanHistory.ColumnsBelow([], "354118"));
    }

    [Fact]
    public void The_same_spot_one_storey_up_matches_within_snapping_and_drawing_tolerance()
    {
        var spot = new PlanHistory.Spot(1000, 1000, 250, 1100);

        Assert.True(PlanHistory.Matches(spot, 1050, 990, 1110, 250));
        Assert.False(PlanHistory.Matches(spot, 1300, 1000, 1100, 250));
        Assert.False(PlanHistory.Matches(spot, 1000, 1000, 1100, 300));
    }
}
