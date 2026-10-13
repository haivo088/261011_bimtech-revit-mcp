namespace Bimtech.RevitMcp.Commands;

// One add-in method. Execute always runs on Revit's API thread, inside
// RevitRequestHandler.Execute, so it may read the model freely.
// Return any object System.Text.Json can serialize; it becomes the JSON-RPC result.
// Throw RpcException for errors Claude should see with a specific code.
public interface IRevitCommand
{
    // Must match "method" in the server's tool definition (server/src/tools/*.ts).
    string Method { get; }

    object? Execute(CommandContext context);
}
