using Bimtech.RevitMcp.Planning;
using Bimtech.RevitMcp.Ui;

namespace Bimtech.RevitMcp.Commands.Plans;

// Opens the review window again, e.g. after the person closed it.
// Returns at once; it never waits for the person to approve.
public sealed class OpenPlanReviewCommand(PlanService plans, ReviewWindowHost review) : IRevitCommand
{
    public string Method => "open_plan_review";
    public CommandAccess Access => CommandAccess.Read;

    public object? Execute(CommandContext context)
    {
        var document = PlanStorageLocation.For(context.RequireDocument());
        var plan = plans.Get(document, context.Params.RequireString("planId", 64));
        review.Show(plan, context.UIApplication);
        return new
        {
            plan = PlanResults.Status(plan),
            reviewWindow = "opened",
        };
    }
}
