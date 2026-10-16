using Bimtech.RevitMcp.Settings;

namespace Bimtech.RevitMcp.Commands.Diagnostics;

// Confirms the bridge works end to end. "message" proves params reach Revit,
// and "commands" lets the server hide tools the user switched off in the settings window.
public sealed class PingCommand : IRevitCommand
{
    public string Method => "ping";
    public CommandAccess Access => CommandAccess.Read;

    public object? Execute(CommandContext context) => new
    {
        status = "ok",
        revitVersion = context.UIApplication.Application.VersionNumber,
        bridgeVersion = App.Version,
        // "not installed" for users; developers see whether Bimtech.RevitMcp.DevTools.dll loaded.
        devTools = DevToolsLoader.Status,
        echo = context.Params.GetString("message", maxLength: 200),
        methods = context.Registry.Methods.Order().ToArray(),
        readOnlyMode = context.Settings.ReadOnlyMode,
        commands = context.Registry.Commands.Select(command => new
        {
            method = command.Method,
            access = command.Access.ToString().ToLowerInvariant(),
            enabled = CommandPolicy.IsEnabled(command, context.Settings),
            available = CommandPolicy.IsAvailable(command, context.Settings),
        }).ToArray(),
    };
}
