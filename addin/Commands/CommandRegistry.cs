using Bimtech.RevitMcp.Commands.Diagnostics;
using Bimtech.RevitMcp.Commands.Documents;
using Bimtech.RevitMcp.Commands.Elements;
using Bimtech.RevitMcp.Commands.Parameters;
using Bimtech.RevitMcp.Commands.Plans;
using Bimtech.RevitMcp.Commands.Structural;
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
    // devTools: the developer-only commands of Bimtech.RevitMcp.DevTools.dll, empty for users (see DevToolsLoader).
    public static CommandRegistry CreateDefault(
        PlanService plans, ReviewWindowHost review, ModelChangeTracker changes, IEnumerable<IRevitCommand> devTools) => new(
    [
        .. devTools,
        new PingCommand(),
        new GetDocumentInfoCommand(),
        new ProposeSetParametersCommand(plans, review, changes),
        new GetPlanCommand(plans),
        new GetPlanStatusCommand(plans),
        new OpenPlanReviewCommand(plans, review),
        new CancelPlanCommand(plans),
        new CommitPlanCommand(plans, new PlanCommitter(plans, new JsonPlanStore()), PlanActions.CreateDefault(), changes),
        new GetLevelsCommand(),
        new ListCategoriesCommand(),
        new GetElementsCommand(),
        new GetSelectedElementsCommand(),
        new GetElementParametersCommand(),
        new ListCadSourcesCommand(),
        new SuggestLevelsFromFolderCommand(),
        new ProposeLevelsCommand(plans, review, changes),
        new ProposeGridsCommand(plans, review, changes),
        new ScanCadWallsCommand(),
        new ProposeWallsCommand(plans, review, changes),
        new ProposeFloorsCommand(plans, review, changes),
        new LoadDefaultFamilyCommand(),
        new ScanCadColumnsCommand(),
        new ProposeColumnsCommand(plans, review, changes),
        new ProposeBeamsCommand(plans, review, changes),
        new ScanCadPilesCommand(),
        new ProposePilesCommand(plans, review, changes),
        new UndoBatchCommand(),
    ]);

    public IReadOnlyCollection<string> Methods => _commands.Keys;

    // Commands sorted by group, then method, as shown in the settings window.
    public IReadOnlyList<IRevitCommand> Commands =>
        _commands.Values.OrderBy(GroupOf).ThenBy(command => command.Method, StringComparer.Ordinal).ToList();

    // The folder a command lives in (Diagnostics, Documents, Elements, Structural...).
    public static string GroupOf(IRevitCommand command) =>
        command.GetType().Namespace?.Split('.').LastOrDefault() ?? "Other";

    public bool TryGet(string method, out IRevitCommand command) =>
        _commands.TryGetValue(method, out command!);
}
