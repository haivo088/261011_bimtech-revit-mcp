namespace Bimtech.RevitMcp.Commands.Diagnostics;

// Confirms the bridge works end to end. "message" proves params reach Revit,
// and "methods" lets the server compare its tools with this add-in's commands.
public sealed class PingCommand : IRevitCommand
{
    public string Method => "ping";

    public object? Execute(CommandContext context) => new
    {
        status = "ok",
        revitVersion = context.UIApplication.Application.VersionNumber,
        bridgeVersion = App.Version,
        echo = context.Params.GetString("message", maxLength: 200),
        methods = context.Registry.Methods.Order().ToArray(),
    };
}
