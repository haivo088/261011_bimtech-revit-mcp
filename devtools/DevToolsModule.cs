using Bimtech.RevitMcp.Commands;
using Bimtech.RevitMcp.Planning.Actions;

namespace Bimtech.RevitMcp.DevTools;

// The developer-only commands. Each must have a matching tool in server/src/tools/dev.ts, which the server
// only registers when BIMTECH_DEV=1.
public sealed class DevToolsModule : IDevToolsModule
{
    public IEnumerable<IRevitCommand> CreateCommands(DevToolsHost host) =>
    [
        new SendCodeCommand(host),
        new CommandSetCommand(),
        new ExportCadCaseCommand(),
        new RunRecognitionCommand(),
        new AnalyzeCadCommand(),
    ];

    public IEnumerable<IPlanAction> CreateActions() => [new RunCodeAction()];
}
