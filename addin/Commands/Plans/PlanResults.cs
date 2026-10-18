using Bimtech.RevitMcp.Planning;

namespace Bimtech.RevitMcp.Commands.Plans;

// Shared result shape for plan commands, so Claude always sees the same fields.
public static class PlanResults
{
    public static object Status(Plan plan) => new
    {
        planId = plan.PlanId,
        kind = plan.Kind,
        title = plan.Title,
        status = plan.Status,
        // Set when the person approves in the review window. commit_plan will require it.
        approvalId = plan.Approval?.ApprovalId,
        // True when the person's auto-commit setting approved a plan with nothing to review: commit it straight away.
        autoApproved = plan.Approval?.Automatic == true,
        createdAt = plan.CreatedAt,
        updatedAt = plan.UpdatedAt,
        summary = plan.Summarize(),
    };
}
