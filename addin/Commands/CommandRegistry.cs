using Bimtech.RevitMcp.Commands.Diagnostics;
using Bimtech.RevitMcp.Commands.Documents;
using Bimtech.RevitMcp.Commands.Elements;
using Bimtech.RevitMcp.Commands.Structural;

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
    public static CommandRegistry CreateDefault() => new(
    [
        new PingCommand(),
        new GetDocumentInfoCommand(),
        new GetLevelsCommand(),
        new ListCategoriesCommand(),
        new GetElementsCommand(),
        new GetSelectedElementsCommand(),
        new GetElementParametersCommand(),
        new ListCadSourcesCommand(),
        new LoadDefaultFamilyCommand(),
        new ScanCadColumnsCommand(),
        new PlaceColumnsCommand(),
        new ScanCadBeamsCommand(),
        new PlaceBeamsCommand(),
        new ScanCadPilesCommand(),
        new PlacePilesCommand(),
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
