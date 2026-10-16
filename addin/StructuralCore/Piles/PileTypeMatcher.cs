using Autodesk.Revit.DB;

namespace Bimtech.StructuralCore.Piles;

/// <summary>Khớp Type cọc theo tham số Diameter của Type.</summary>
internal static class PileTypeMatcher
{
    public const string PileDiameterParam = "Diameter";

    /// <summary>Ngưỡng coi Type đã khớp đúng đường kính nhóm (mm) — giống ColumnTypeMatcher.MatchToleranceMm.</summary>
    public const double MatchToleranceMm = 2.0;

    /// <summary>Mọi Type trong category Structural Foundations (cọc và móng đơn đều nằm ở đây).</summary>
    public static List<FamilySymbol> CollectFoundationTypes(Document doc) =>
        new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_StructuralFoundation)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .ToList();

    public static double? DiameterMm(FamilySymbol symbol) =>
        SymbolParameters.ReadLengthMm(symbol, PileDiameterParam);

    public static bool IsMatch(FamilySymbol symbol, double diameterMm)
    {
        var d = DiameterMm(symbol);
        return d is not null && Math.Abs(d.Value - diameterMm) <= MatchToleranceMm;
    }

    public static T? FindExactMatch<T>(double diameterMm, IEnumerable<T> choices, Func<T, FamilySymbol> symbolOf) where T : class =>
        choices.FirstOrDefault(choice => IsMatch(symbolOf(choice), diameterMm));

    /// <summary>Type có Diameter gần đúng nhất với đường kính nhóm cọc — null nếu không Type nào có parameter Diameter.</summary>
    public static T? AutoMatch<T>(double diameterMm, IEnumerable<T> choices, Func<T, FamilySymbol> symbolOf) where T : class
    {
        T? best = null;
        var bestDiff = double.MaxValue;

        foreach (var choice in choices)
        {
            var diameter = DiameterMm(symbolOf(choice));
            if (diameter is null)
                continue;

            var diff = Math.Abs(diameter.Value - diameterMm);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = choice;
            }
        }

        return best;
    }

    /// <summary>Tên Family của mọi Type có parameter Diameter (đặc trưng riêng của Type cọc, không phải đài).</summary>
    public static HashSet<string> FindPileFamilyNames(IEnumerable<FamilySymbol> types) =>
        types.Where(s => DiameterMm(s) is not null).Select(s => s.Family.Name).ToHashSet();
}
