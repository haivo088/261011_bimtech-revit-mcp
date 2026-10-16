using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Bimtech.RevitMcp.Recognition;

// What a DWG file name says about its storey, e.g. "KC-03 MB TANG 2 (+3.600).dwg" → "Tầng 2" at 3600 mm.
// Name and elevation are null when the file name does not say; nothing is invented.
// Order sorts storeys bottom to top when elevations are missing: foundation, basements, ground, 1, 2... roof, top.
public sealed record StoreyGuess(
    string File, string? Name, double? ElevationMm, double Order, bool LooksLikePlan, IReadOnlyList<string> Notes);

public static partial class StoreyNames
{
    // Storey heights outside this range are reported as unusual (typos like 36000 for 3600).
    public const double MinStoreyHeightMm = 2000;
    public const double MaxStoreyHeightMm = 8000;

    public static StoreyGuess Parse(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var text = Normalize(stem);
        var notes = new List<string>();

        var (name, order) = StoreyName(text);
        if (Typical().IsMatch(text))
        {
            name = null;
            notes.Add("Mặt bằng tầng điển hình: dùng cho nhiều tầng, hỏi người dùng các tầng áp dụng.");
        }
        var elevation = Elevation(stem);
        var looksLikePlan = !NotAPlan().IsMatch(text);
        if (!looksLikePlan) notes.Add("Tên file giống mặt cắt, chi tiết hoặc bố trí thép, không phải mặt bằng.");
        if (name is null) notes.Add("Không đọc được tên tầng từ tên file.");
        if (elevation is null) notes.Add("Không đọc được cao độ từ tên file.");
        return new StoreyGuess(fileName, name, elevation, order, looksLikePlan, notes);
    }

    // Plans only, bottom to top; storeys named by several files are kept and reported.
    public static (List<StoreyGuess> Storeys, List<StoreyGuess> Skipped, List<string> Warnings) SuggestTable(IEnumerable<string> fileNames)
    {
        var guesses = fileNames.Select(Parse).ToList();
        var plans = guesses.Where(guess => guess.LooksLikePlan).ToList();
        // By elevation where the file name gives one; the rest by storey order after them.
        var storeys = plans.Where(guess => guess.ElevationMm is not null)
            .OrderBy(guess => guess.ElevationMm).ThenBy(guess => guess.Order)
            .Concat(plans.Where(guess => guess.ElevationMm is null)
                .OrderBy(guess => guess.Order).ThenBy(guess => guess.File, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var warnings = new List<string>();
        foreach (var group in storeys.Where(guess => guess.Name is not null).GroupBy(guess => guess.Name!, StringComparer.OrdinalIgnoreCase))
            if (group.Count() > 1)
                warnings.Add($"\"{group.Key}\" có {group.Count()} file: {string.Join(", ", group.Select(guess => guess.File))}. Chọn một.");
        var withElevation = storeys.Where(guess => guess.ElevationMm is not null && guess.Name is not null).ToList();
        foreach (var (lower, upper) in withElevation.Zip(withElevation.Skip(1)))
            if (upper.Order < lower.Order)
                warnings.Add($"Thứ tự theo tên ({lower.Name}, {upper.Name}) ngược với thứ tự theo cao độ: kiểm tra lại cao độ.");
        warnings.AddRange(HeightWarnings(withElevation
            .GroupBy(guess => guess.Name!, StringComparer.OrdinalIgnoreCase)
            .Select(group => (group.Key, group.First().ElevationMm!.Value))));
        return (storeys, guesses.Where(guess => !guess.LooksLikePlan).ToList(), warnings);
    }

    // Storey heights between consecutive levels (sorted by elevation) outside MinStoreyHeightMm..MaxStoreyHeightMm.
    public static List<string> HeightWarnings(IEnumerable<(string Name, double ElevationMm)> levels)
    {
        var sorted = levels.OrderBy(level => level.ElevationMm).ToList();
        var warnings = new List<string>();
        foreach (var (lower, upper) in sorted.Zip(sorted.Skip(1)))
        {
            var height = upper.ElevationMm - lower.ElevationMm;
            if (height is > 0 and (< MinStoreyHeightMm or > MaxStoreyHeightMm))
                warnings.Add($"Chiều cao tầng {lower.Name} → {upper.Name} là {height:0.##} mm, bất thường "
                    + $"(thường {MinStoreyHeightMm / 1000:0.#}–{MaxStoreyHeightMm / 1000:0.#} m). Kiểm tra cao độ có gõ nhầm không.");
        }
        return warnings;
    }

    // ---- Storey name

    private static (string? Name, double Order) StoreyName(string text)
    {
        if (Basement().Match(text) is { Success: true } basement)
        {
            var number = basement.Groups["n"].Success ? int.Parse(basement.Groups["n"].Value, CultureInfo.InvariantCulture) : 1;
            return ($"Hầm {number}", -10 * number);
        }
        if (Foundation().IsMatch(text)) return ("Móng", -1000);
        if (Ground().IsMatch(text)) return ("Tầng trệt", 1);
        if (Mezzanine().IsMatch(text)) return ("Tầng lửng", 1.5);
        if (Terrace().IsMatch(text)) return ("Sân thượng", 900);
        if (Roof().IsMatch(text)) return ("Mái", 1000);
        if (Top().IsMatch(text)) return ("Tum", 1001);
        if (Floor().Match(text) is { Success: true } floor)
        {
            var number = int.Parse(floor.Groups["n"].Value, CultureInfo.InvariantCulture);
            return ($"Tầng {number}", number);
        }
        return (null, 500);
    }

    // ---- Elevation in meters: "(+3.600)", " -1,500", "±0.000", "COS 3.60". A sign counts only at the start, after a
    // space or an opening bracket, so sheet numbers like "KC-2.01" or "KC-301" are not read as elevations.

    private static double? Elevation(string stem)
    {
        var text = stem.Replace('±', '+');
        if (MetersSigned().Match(text) is { Success: true } signed) return Meters(signed);
        if (MetersAfterKeyword().Match(Normalize(text)) is { Success: true } keyword) return Meters(keyword);
        return null;
    }

    private static double Meters(Match match)
    {
        var value = double.Parse($"{match.Groups["int"].Value}.{match.Groups["frac"].Value}", CultureInfo.InvariantCulture);
        return Math.Round((match.Groups["sign"].Value == "-" ? -1 : 1) * value * 1000, 1);
    }

    // Upper case, no Vietnamese diacritics, separators as single spaces: "Mặt_bằng-tầng 2" → "MAT BANG TANG 2".
    private static string Normalize(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(c);
        var plain = builder.ToString().Normalize(NormalizationForm.FormC).Replace('Đ', 'D').Replace('đ', 'd').ToUpperInvariant();
        return Spaces().Replace(Separators().Replace(plain, " "), " ").Trim();
    }

    [GeneratedRegex(@"[_\-\(\)\[\]]")] private static partial Regex Separators();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    [GeneratedRegex(@"\b(TANG HAM|HAM)\s*(?<n>\d+)?\b|\bB(?<n>\d)\b")] private static partial Regex Basement();
    [GeneratedRegex(@"\b(MONG|DAI MONG|GIANG MONG|COC)\b")] private static partial Regex Foundation();
    [GeneratedRegex(@"\bTRET\b")] private static partial Regex Ground();
    [GeneratedRegex(@"\bLUNG\b")] private static partial Regex Mezzanine();
    [GeneratedRegex(@"\bSAN THUONG\b")] private static partial Regex Terrace();
    [GeneratedRegex(@"\bMAI\b|\bROOF\b")] private static partial Regex Roof();
    [GeneratedRegex(@"\bTUM\b")] private static partial Regex Top();
    [GeneratedRegex(@"\b(TANG|LAU|FLOOR|LEVEL|L|T)\s*(?<n>\d{1,3})\b")] private static partial Regex Floor();
    [GeneratedRegex(@"\b(MAT CAT|MC|CHI TIET|CT|THEP|BO TRI THEP|THONG KE|GHI CHU|TONG QUAN)\b")] private static partial Regex NotAPlan();
    [GeneratedRegex(@"(?<=^|[\s(\[_])(?<sign>[+-])\s?(?<int>\d{1,3})[.,](?<frac>\d{1,3})(?![\d])")] private static partial Regex MetersSigned();
    [GeneratedRegex(@"\b(COS|COT|EL|ELEV|CAO DO)\s*(?<sign>[+-])?\s?(?<int>\d{1,3})[.,](?<frac>\d{1,3})\b")] private static partial Regex MetersAfterKeyword();
    [GeneratedRegex(@"\bDIEN HINH\b")] private static partial Regex Typical();
}
