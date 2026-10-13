using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands.Common;
using Bimtech.RevitMcp.Protocol;
using Bimtech.StructuralCore.Families;

namespace Bimtech.RevitMcp.Commands.Structural;

// Loads the company concrete column family (b/h parameters) packaged with the add-in, so place_columns
// can create sized types in it. Does nothing if the model already has it.
public sealed class LoadDefaultColumnFamilyCommand : IRevitCommand
{
    public string Method => "load_default_column_family";

    public object? Execute(CommandContext context)
    {
        var document = context.RequireWritableDocument();
        var existing = DefaultFamilies.Find(document, DefaultFamilies.ColumnFamilyName);
        string? batchId = null;
        var family = existing;
        if (family is null)
        {
            using var guard = new FailureGuard(context.UIApplication.Application);
            try
            {
                family = DefaultFamilies.Load(document, DefaultFamilies.ColumnFamilyName);
            }
            catch (InvalidOperationException exception)
            {
                throw new RpcException(RpcErrorCodes.RevitApiError, exception.Message, new { revitErrors = guard.Errors });
            }
            batchId = context.Sessions.Add(WriteBatch.Kind, document,
                new WriteBatch($"Nạp family {family.Name}", [family.Id], []));
        }

        var types = family.GetFamilySymbolIds().Select(document.GetElement).OfType<FamilySymbol>().ToList();
        return new
        {
            alreadyLoaded = existing is not null,
            familyId = RevitJson.Id(family.Id),
            familyName = family.Name,
            types = types.Select(StructuralJson.ColumnType).ToList(),
            batchId,
            next = "Use familyId as templateFamilyId in place_columns (dryRun=true first).",
        };
    }
}
