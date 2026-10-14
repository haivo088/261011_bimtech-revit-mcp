using Bimtech.RevitMcp.Planning;

namespace Bimtech.RevitMcp.Commands.Plans;

// Abandons a plan. The model is never touched.
public sealed class CancelPlanCommand(PlanService plans) : IRevitCommand
{
    public string Method => "cancel_plan";

    public object? Execute(CommandContext context)
    {
        var document = PlanStorageLocation.For(context.RequireDocument());
        return PlanResults.Status(plans.Cancel(document, context.Params.RequireString("planId", 64)));
    }
}
