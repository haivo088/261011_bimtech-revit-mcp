using System.Text.Json;
using Bimtech.RevitMcp.Commands;
using Bimtech.RevitMcp.Protocol;

namespace Bimtech.RevitMcp.Settings;

// The one place that decides whether a command may run. The add-in enforces it on every request;
// the server only uses "available" from ping to hide tools, which is a convenience, not a safeguard.
public static class CommandPolicy
{
    // Diagnostics the user cannot switch off: without ping the server cannot even see the settings.
    public static readonly IReadOnlySet<string> AlwaysEnabled = new HashSet<string>(StringComparer.Ordinal) { "ping" };

    private static readonly CommandParams NoParams = new(default(JsonElement));

    public static bool IsEnabled(IRevitCommand command, McpSettings settings) =>
        AlwaysEnabled.Contains(command.Method) || !settings.IsDisabled(command.Method);

    // Whether Claude should see the tool. In read-only mode a write command stays visible only if its
    // default call is a preview (e.g. a dryRun flag that defaults to true).
    public static bool IsAvailable(IRevitCommand command, McpSettings settings) =>
        IsEnabled(command, settings) && !(settings.ReadOnlyMode && command.ModifiesModel(NoParams));

    // Checked on the TCP thread before the request waits for Revit.
    public static void EnsureAllowed(IRevitCommand command, CommandParams parameters, McpSettings settings)
    {
        if (!IsEnabled(command, settings))
            throw new RpcException(RpcErrorCodes.CommandDisabled,
                $"'{command.Method}' is turned off in BIMTECH MCP Settings in Revit. Ask the user to enable it there.",
                new { method = command.Method, reason = "disabled" });

        if (settings.ReadOnlyMode && command.ModifiesModel(parameters))
            throw new RpcException(RpcErrorCodes.CommandDisabled,
                "Read-only mode is on in BIMTECH MCP Settings, and this call would change the model. "
                + "Ask the user to turn read-only mode off in Revit if the change is intended.",
                new { method = command.Method, reason = "readOnlyMode" });
    }
}
