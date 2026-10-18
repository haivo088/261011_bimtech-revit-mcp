using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Bimtech.RevitMcp.Recognition;

// Which Basic Wall type a structural wall group of a given thickness uses, like an engineer picks it:
//   1. the company type "BTC-VachKC_<b>" of exactly that thickness;
//   2. else an existing concrete wall type (structural layer of concrete, or a name saying bê tông/concrete/BTCT/vách)
//      within 1 mm;
//   3. else none: the caller creates "BTC-VachKC_<b>".
// Template walls of the Revit templates (CW 102-85-100p, Brick, Masonry, Stud, Render, Block...) are never a match,
// even when their thickness is right. Revit-free: WallPlacer describes each WallType as a Candidate.
public static partial class WallTypes
{
    public const string CompanyPrefix = "BTC-VachKC_";
    // Thicknesses are whole millimetres in the company types; 0.5 mm only absorbs feet/mm rounding.
    public const double CompanyToleranceMm = 0.5;
    public const double ConcreteToleranceMm = 1.0;

    // StructureIsConcrete: a layer with the Structure function has a concrete material (class or name).
    // HasLayers: the type has a layer structure, so it can be duplicated and resized.
    public sealed record Candidate(string Name, double WidthMm, bool StructureIsConcrete, bool HasLayers = true);

    public static string NewTypeName(double widthMm) => $"{CompanyPrefix}{Math.Round(widthMm):0}";

    public static bool IsCompanyType(string name) => name.StartsWith(CompanyPrefix, StringComparison.OrdinalIgnoreCase);

    // Revit template walls: cavity walls "CW ...", brick, masonry, stud, render, block, generic, partition, plaster...
    public static bool IsGeneric(string name) => GenericName().IsMatch(Plain(name));

    public static bool NameSaysConcrete(string name) => ConcreteName().IsMatch(Plain(name));

    // A material name or class such as "Bê tông B25", "Concrete, Cast-in-Place gray", "BTCT".
    public static bool MaterialSaysConcrete(string? text) => text is not null && ConcreteMaterial().IsMatch(Plain(text));

    // 0 = company type of exactly that thickness, 1 = concrete type within 1 mm, null = not an exact match.
    public static int? Rank(Candidate type, double widthMm)
    {
        if (IsGeneric(type.Name)) return null;
        var difference = Math.Abs(type.WidthMm - widthMm);
        if (IsCompanyType(type.Name) && difference <= CompanyToleranceMm) return 0;
        if ((type.StructureIsConcrete || NameSaysConcrete(type.Name)) && difference <= ConcreteToleranceMm) return 1;
        return null;
    }

    public static bool IsExactMatch(Candidate type, double widthMm) => Rank(type, widthMm) is not null;

    // The best exact match: company type first, then the closest thickness, then by name. companyOnly: only rank 0,
    // for groups the user asked a new company type for.
    public static T? FindExact<T>(IEnumerable<T> choices, Func<T, Candidate> describe, double widthMm, bool companyOnly = false)
        where T : class =>
        choices.Select(choice => (Choice: choice, Type: describe(choice)))
            .Select(c => (c.Choice, c.Type, Rank: Rank(c.Type, widthMm)))
            .Where(c => c.Rank is not null && (!companyOnly || c.Rank == 0))
            .OrderBy(c => c.Rank).ThenBy(c => Math.Abs(c.Type.WidthMm - widthMm))
            .ThenBy(c => c.Type.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => c.Choice).FirstOrDefault();

    // The type shown as nearestType when there is no exact match: closest thickness, template walls last.
    public static T? FindNearest<T>(IEnumerable<T> choices, Func<T, Candidate> describe, double widthMm) where T : class =>
        choices.Select(choice => (Choice: choice, Type: describe(choice)))
            .OrderBy(c => IsGeneric(c.Type.Name)).ThenBy(c => Math.Abs(c.Type.WidthMm - widthMm))
            .ThenBy(c => c.Type.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => c.Choice).FirstOrDefault();

    // The type duplicated to make "BTC-VachKC_<b>": an existing company type, else a concrete type, else any type
    // with layers; closest thickness first within each.
    public static T? FindTemplate<T>(IEnumerable<T> choices, Func<T, Candidate> describe, double widthMm) where T : class =>
        choices.Select(choice => (Choice: choice, Type: describe(choice)))
            .Where(c => c.Type.HasLayers)
            .OrderBy(c => IsCompanyType(c.Type.Name) ? 0
                : !IsGeneric(c.Type.Name) && (c.Type.StructureIsConcrete || NameSaysConcrete(c.Type.Name)) ? 1 : 2)
            .ThenBy(c => Math.Abs(c.Type.WidthMm - widthMm))
            .ThenBy(c => c.Type.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => c.Choice).FirstOrDefault();

    // Lower case without Vietnamese marks: "Vách BTCT" → "vach btct", "Đ" → "d".
    private static string Plain(string text)
    {
        var decomposed = text.Replace('Đ', 'D').Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var plain = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) plain.Append(char.ToLowerInvariant(c));
        return plain.ToString();
    }

    [GeneratedRegex(@"^(cw|sw|ew|iw)[\s_-]*\d|brick|masonry|\bstud|render|\bblock|generic|partition|plaster|cavity|timber|\bsip\b|\bgach\b")]
    private static partial Regex GenericName();

    [GeneratedRegex(@"be\s*tong|concrete|btct|\bvach")]
    private static partial Regex ConcreteName();

    [GeneratedRegex(@"be\s*tong|concrete|btct")]
    private static partial Regex ConcreteMaterial();
}
