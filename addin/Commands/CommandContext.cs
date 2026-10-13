using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Bimtech.RevitMcp.Protocol;

namespace Bimtech.RevitMcp.Commands;

// Everything a command needs for one request.
public sealed class CommandContext(
    UIApplication uiApplication,
    CommandParams parameters,
    CommandRegistry registry,
    SessionStore sessions,
    CancellationToken cancellation)
{
    public UIApplication UIApplication { get; } = uiApplication;
    public CommandParams Params { get; } = parameters;
    public CommandRegistry Registry { get; } = registry;

    // Scan results and write batches kept between calls; see SessionStore.
    public SessionStore Sessions { get; } = sessions;

    // Cancelled when the client times out or disconnects. Long loops should call
    // Cancellation.ThrowIfCancellationRequested() so Revit is not kept busy for nothing.
    public CancellationToken Cancellation { get; } = cancellation;

    public UIDocument RequireUIDocument() =>
        UIApplication.ActiveUIDocument
        ?? throw new RpcException(RpcErrorCodes.NoDocument, "Open a Revit model, then try again.");

    public Document RequireDocument() => RequireUIDocument().Document;

    // For commands that change the model. Checks what Revit would otherwise reject halfway through.
    public Document RequireWritableDocument()
    {
        var document = RequireDocument();
        if (document.IsFamilyDocument)
            throw new RpcException(RpcErrorCodes.NotWritable, "The active document is a family. Open a project model.");
        if (document.IsReadOnly)
            throw new RpcException(RpcErrorCodes.NotWritable, "The active model is read-only.");
        if (document.IsModifiable)
            throw new RpcException(RpcErrorCodes.NotWritable,
                "Revit is already inside a transaction. Finish the current Revit command, then retry.");
        return document;
    }
}
