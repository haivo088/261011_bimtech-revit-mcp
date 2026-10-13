using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands.Common;
using Bimtech.RevitMcp.Protocol;

namespace Bimtech.RevitMcp.Commands.Structural;

// Deletes exactly what one write command created, identified by its batchId.
// Elements already removed (e.g. by Ctrl+Z) are skipped. Created types are deleted only when unused,
// so a type someone has since placed elsewhere is kept.
public sealed class UndoBatchCommand : IRevitCommand
{
    public string Method => "undo_batch";

    public object? Execute(CommandContext context)
    {
        var document = context.RequireWritableDocument();
        var batchId = context.Params.RequireString("batchId", maxLength: 64);
        var batch = context.Sessions.Require<WriteBatch>(WriteBatch.Kind, batchId, document, "batchId");

        var existing = batch.ElementIds.Where(id => document.GetElement(id) is not null).ToList();
        var alreadyGone = batch.ElementIds.Count - existing.Count;

        // Deleting a family deletes every instance of it, including ones placed by later calls. Keep families in use.
        var keptFamilies = existing.Select(document.GetElement).OfType<Family>().Where(family => IsUsed(document, family)).ToList();
        var elements = existing.Except(keptFamilies.Select(family => family.Id)).ToList();

        using var guard = new FailureGuard(context.UIApplication.Application);
        using var transaction = new Transaction(document, $"BIMTECH MCP: Hoàn tác {batch.Description}");
        transaction.Start();

        if (elements.Count > 0) document.Delete(elements);

        var deletedTypes = new List<string>();
        var keptTypes = new List<FamilySymbol>();
        foreach (var typeId in batch.TypeIds)
        {
            if (document.GetElement(typeId) is not FamilySymbol type) continue;
            var inUse = new FilteredElementCollector(document).WhereElementIsNotElementType()
                .WherePasses(new FamilyInstanceFilter(document, typeId)).Any();
            if (inUse)
            {
                keptTypes.Add(type);
                continue;
            }
            deletedTypes.Add(type.Name);
            document.Delete(typeId);
        }

        if (transaction.Commit() != TransactionStatus.Committed || guard.Errors.Count > 0)
            throw new RpcException(RpcErrorCodes.RevitApiError,
                "Revit rejected the undo; nothing was deleted. " + string.Join(" | ", guard.Errors),
                new { revitErrors = guard.Errors });

        // Whatever was kept because it is in use stays undoable under the same batchId.
        var remaining = keptFamilies.Count + keptTypes.Count;
        if (remaining == 0)
            context.Sessions.Remove(batchId);
        else
            context.Sessions.Replace(batchId, new WriteBatch(batch.Description,
                keptFamilies.Select(family => family.Id).ToList(), keptTypes.Select(type => type.Id).ToList()));

        return new
        {
            batchId,
            batchRemains = remaining > 0,
            deletedElements = elements.Count,
            alreadyGone,
            deletedTypes,
            keptTypesInUse = keptTypes.Select(type => type.Name).ToList(),
            keptFamiliesInUse = keptFamilies.Select(family => family.Name).ToList(),
        };
    }

    private static bool IsUsed(Document document, Family family) =>
        family.GetFamilySymbolIds().Any(symbolId =>
            new FilteredElementCollector(document).WhereElementIsNotElementType()
                .WherePasses(new FamilyInstanceFilter(document, symbolId)).Any());
}
