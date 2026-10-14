using Bimtech.RevitMcp.Commands.Diagnostics;
using Bimtech.RevitMcp.Commands.Documents;
using Bimtech.RevitMcp.Commands.Parameters;
using Bimtech.RevitMcp.Commands.Plans;
using Bimtech.RevitMcp.Planning;
using Bimtech.RevitMcp.Planning.Actions;
using Bimtech.RevitMcp.Ui;

namespace Bimtech.RevitMcp.Commands;

// Maps method names to commands. Lookup happens on the TCP thread,
// so unknown methods fail without waiting for Revit.
public sealed class CommandRegistry
{
    private readonly Dictionary<string, IRevitCommand> _commands = new(StringComparer.Ordinal);

    public CommandRegistry(IEnumerable<IRevitCommand> commands)
    {
        foreach (var command in commands)
        {
            if (string.IsNullOrWhiteSpace(command.Method))
                throw new InvalidOperationException($"{command.GetType().Name} has no method name.");
            if (!_commands.TryAdd(command.Method, command))
                throw new InvalidOperationException($"Duplicate command method: {command.Method}");
        }
    }

    // To add a command: create a class implementing IRevitCommand and list it here.
    // server/tests/contract.test.mjs checks that each method has a matching server tool.
    // The plan service and review window outlive the bridge (see App), so an open
    // review window keeps working while the bridge is stopped and restarted.
    public static CommandRegistry CreateDefault(PlanService plans, ReviewWindowHost review, ModelChangeTracker changes) => new(
    [
        new PingCommand(),
        new GetDocumentInfoCommand(),
        new ProposeSetParametersCommand(plans, review, changes),
        new GetPlanCommand(plans),
        new GetPlanStatusCommand(plans),
        new OpenPlanReviewCommand(plans, review),
        new CancelPlanCommand(plans),
        new CommitPlanCommand(plans, new PlanCommitter(plans, new JsonPlanStore()), PlanActions.CreateDefault(), changes),
    ]);

    public IReadOnlyCollection<string> Methods => _commands.Keys;

    public bool TryGet(string method, out IRevitCommand command) =>
        _commands.TryGetValue(method, out command!);
}
