using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Protocol;

namespace Bimtech.RevitMcp.Commands.Common;

// The frame every write command runs in:
//   - FailureGuard, so no Revit dialog can block the bridge;
//   - one TransactionGroup, so the whole call is a single Ctrl+Z in Revit;
//   - Complete() keeps the changes and stores a WriteBatch for undo_batch, or rolls back when Revit
//     reported errors (and throws) or nothing was created.
// Anything that leaves without Complete() is rolled back by Dispose().
//
//   using var write = new WriteSession(context, document, "BIMTECH MCP: Đặt dầm");
//   ... core calls that open their own Transactions ...
//   var batchId = write.Complete("Đặt 12 dầm", createdIds, createdTypeIds);
public sealed class WriteSession : IDisposable
{
    private readonly CommandContext _context;
    private readonly Document _document;
    private readonly TransactionGroup _group;

    public WriteSession(CommandContext context, Document document, string name)
    {
        _context = context;
        _document = document;
        Guard = new FailureGuard(context.UIApplication.Application);
        _group = new TransactionGroup(document, name);
        _group.Start();
    }

    public FailureGuard Guard { get; }

    // Returns the batchId, or null when nothing was created and the group was rolled back.
    public string? Complete(string description, IReadOnlyList<ElementId> elementIds, IReadOnlyList<ElementId> typeIds)
    {
        if (Guard.Errors.Count > 0)
        {
            _group.RollBack();
            throw new RpcException(RpcErrorCodes.RevitApiError,
                "Revit rejected the changes; nothing was kept. " + string.Join(" | ", Guard.Errors),
                new { revitErrors = Guard.Errors });
        }
        if (elementIds.Count == 0 && typeIds.Count == 0)
        {
            _group.RollBack();
            return null;
        }
        _group.Assimilate();
        return _context.Sessions.Add(WriteBatch.Kind, _document, new WriteBatch(description, elementIds, typeIds));
    }

    // Warnings Revit raised and the guard dismissed, for the command's result.
    public object WarningReport => new { count = Guard.WarningCount, messages = Guard.Warnings };

    public void Dispose()
    {
        if (_group.HasStarted() && !_group.HasEnded()) _group.RollBack();
        _group.Dispose();
        Guard.Dispose();
    }
}
