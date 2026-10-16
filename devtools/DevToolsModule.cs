using Bimtech.RevitMcp.Commands;

namespace Bimtech.RevitMcp.DevTools;

// The developer-only commands. Each must have a matching tool in server/src/tools/dev.ts, which the server
// only registers when BIMTECH_DEV=1.
public sealed class DevToolsModule : IDevToolsModule
{
    public IEnumerable<IRevitCommand> CreateCommands() =>
    [
        new RunRevitQueryCommand(),
        new ExportCadCaseCommand(),
        new RunRecognitionCommand(),
        new AnalyzeCadCommand(),
    ];
}
