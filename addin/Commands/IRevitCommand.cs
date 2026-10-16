namespace Bimtech.RevitMcp.Commands;

// What a command may do to the model. Drives read-only mode, the settings window badges,
// and must agree with the server's readOnlyHint/destructiveHint (checked by contract.test.mjs).
public enum CommandAccess
{
    Read,   // never changes the model
    Write,  // adds or changes elements
    Delete, // removes elements
}

// One add-in method. Execute always runs on Revit's API thread, inside
// RevitRequestHandler.Execute, so it may read the model freely.
// Return any object System.Text.Json can serialize; it becomes the JSON-RPC result.
// Throw RpcException for errors Claude should see with a specific code.
public interface IRevitCommand
{
    // Must match "method" in the server's tool definition (server/src/tools/*.ts).
    string Method { get; }

    // Required on purpose: a new write command cannot slip through read-only mode by omission.
    CommandAccess Access { get; }

    // Whether this particular call changes the model. Override when a write command has a
    // preview mode (a dryRun flag), so previews still work in read-only mode.
    bool ModifiesModel(CommandParams parameters) => Access != CommandAccess.Read;

    object? Execute(CommandContext context);
}
