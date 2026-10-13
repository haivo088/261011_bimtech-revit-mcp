using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands.Common;
using Bimtech.RevitMcp.Protocol;
using Bimtech.StructuralCore.Families;
using Bimtech.StructuralCore.Piles;

namespace Bimtech.RevitMcp.Commands.Structural;

// Loads a company family packaged with the add-in so place_* can create sized types in it:
//   column → BTC-CotCN_BeTong (b/h), beam → BTC-DamCN_BeTong (b/h), pile → BTC-CocTron_BeTong (Diameter).
// For piles it also creates the default cap and lean-concrete Foundation Slab types when missing.
// Anything already in the model is left alone.
public sealed class LoadDefaultFamilyCommand : IRevitCommand
{
    private static readonly Dictionary<string, string> FamilyByKind = new(StringComparer.OrdinalIgnoreCase)
    {
        ["column"] = DefaultFamilies.ColumnFamilyName,
        ["beam"] = DefaultFamilies.BeamFamilyName,
        ["pile"] = DefaultFamilies.PileFamilyName,
    };

    public string Method => "load_default_family";
    public CommandAccess Access => CommandAccess.Write;

    public object? Execute(CommandContext context)
    {
        var kind = context.Params.RequireString("kind", maxLength: 16);
        if (!FamilyByKind.TryGetValue(kind, out var familyName))
            throw RpcException.InvalidParams("kind", "must be column, beam or pile.");

        var document = context.RequireWritableDocument();
        var existing = DefaultFamilies.Find(document, familyName);
        var slabTypesBefore = PileDefaults.CollectFoundationSlabTypes(document).Select(type => type.Id).ToHashSet();

        using var write = new WriteSession(context, document, $"BIMTECH MCP: Nạp family {familyName}");
        Family family;
        try
        {
            family = existing ?? DefaultFamilies.Load(document, familyName);
        }
        catch (InvalidOperationException exception)
        {
            throw new RpcException(RpcErrorCodes.RevitApiError, exception.Message, new { revitErrors = write.Guard.Errors });
        }

        var slabTypes = kind.Equals("pile", StringComparison.OrdinalIgnoreCase)
            ? PileDefaults.EnsureFoundationSlabTypes(document, PileDefaults.CollectFoundationSlabTypes(document))
            : [];
        var newSlabTypes = slabTypes.Where(type => !slabTypesBefore.Contains(type.Id)).ToList();

        var batchId = write.Complete(
            $"Nạp family {familyName}",
            existing is null ? [family.Id] : [],
            newSlabTypes.Select(type => type.Id).ToList());

        var symbols = family.GetFamilySymbolIds().Select(document.GetElement).OfType<FamilySymbol>().ToList();
        return new
        {
            kind = kind.ToLowerInvariant(),
            alreadyLoaded = existing is not null,
            familyId = RevitJson.Id(family.Id),
            familyName = family.Name,
            types = symbols.Select(symbol => kind.ToLowerInvariant() switch
            {
                "beam" => StructuralJson.BeamType(symbol),
                "pile" => StructuralJson.PileType(symbol),
                _ => StructuralJson.ColumnType(symbol),
            }).ToList(),
            foundationSlabTypes = kind.Equals("pile", StringComparison.OrdinalIgnoreCase)
                ? slabTypes.Select(StructuralJson.SlabType).ToList()
                : null,
            createdSlabTypes = newSlabTypes.Select(type => type.Name).ToList(),
            batchId,
            next = "Use familyId as templateFamilyId in place_columns/place_beams (pile types are created from the nearest pile type).",
        };
    }
}
