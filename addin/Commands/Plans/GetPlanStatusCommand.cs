using Bimtech.RevitMcp.Planning;

namespace Bimtech.RevitMcp.Commands.Plans;

// Lightweight; Claude calls it when the user says they have finished reviewing.
public sealed class GetPlanStatusCommand(PlanService plans) : IRevitCommand
{
    public string Method => "get_plan_status";

    public object? Execute(CommandContext context)
    {
        var document = PlanStorageLocation.For(context.RequireDocument());
        return PlanResults.Status(plans.Get(document, context.Params.RequireString("planId", 64)));
    }
}
