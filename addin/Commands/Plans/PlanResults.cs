using Bimtech.RevitMcp.Planning;

namespace Bimtech.RevitMcp.Commands.Plans;

// Shared result shape for plan commands, so Claude always sees the same fields.
internal static class PlanResults
{
    public static object Status(Plan plan) => new
    {
        planId = plan.PlanId,
        kind = plan.Kind,
        title = plan.Title,
        status = plan.Status,
        // Set by the review window (step 2). commit_plan will require it.
        approvalId = (string?)null,
        createdAt = plan.CreatedAt,
        updatedAt = plan.UpdatedAt,
        summary = plan.Summarize(),
    };
}
