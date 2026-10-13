using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Bimtech.RevitMcp.Protocol;

namespace Bimtech.RevitMcp.Commands;

// Everything a command needs for one request.
public sealed class CommandContext(
    UIApplication uiApplication,
    CommandParams parameters,
    CommandRegistry registry,
    CancellationToken cancellation)
{
    public UIApplication UIApplication { get; } = uiApplication;
    public CommandParams Params { get; } = parameters;
    public CommandRegistry Registry { get; } = registry;

    // Cancelled when the client times out or disconnects. Long loops should call
    // Cancellation.ThrowIfCancellationRequested() so Revit is not kept busy for nothing.
    public CancellationToken Cancellation { get; } = cancellation;

    public UIDocument RequireUIDocument() =>
        UIApplication.ActiveUIDocument
        ?? throw new RpcException(RpcErrorCodes.NoDocument, "Open a Revit model, then try again.");

    public Document RequireDocument() => RequireUIDocument().Document;
}
