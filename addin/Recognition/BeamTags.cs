using System.Globalization;
using System.Text.RegularExpressions;

namespace Bimtech.RevitMcp.Recognition;

// A beam mark, e.g. "D2-X7(220x600)" → name D2-X7, 220 x 600 (b x h, in that order). "220x600" alone has no name;
// "D1" alone has no size. Null when the text is neither.
public sealed record BeamTag(string Text, string? Name, double? BMm, double? HMm);

public static partial class BeamTags
{
    public static BeamTag? Parse(string text)
    {
        var clean = Clean(text);
        if (clean.Length == 0) return null;
        var sizes = Size().Matches(clean);
        if (sizes.Count > 0)
        {
            // The last "bxh": an "X" inside the name (D6-X7) is not the separator.
            var size = sizes[^1];
            var name = clean[..size.Index].Trim(' ', '(', '[', '-', ':', '=', ',');
            return new BeamTag(text, name.Length is > 0 and <= 30 ? name.ToUpperInvariant() : null,
                double.Parse(size.Groups["b"].Value, CultureInfo.InvariantCulture),
                double.Parse(size.Groups["h"].Value, CultureInfo.InvariantCulture));
        }
        return NameOnly().IsMatch(clean) ? new BeamTag(text, clean.Replace(" ", "").ToUpperInvariant(), null, null) : null;
    }

    // One line, plain spaces, without the MText formatting codes AutoCAD texts sometimes carry ({\fArial;..}, \P).
    private static string Clean(string text)
    {
        var plain = Formatting().Replace(text.Replace("\\P", " "), "").Replace("{", "").Replace("}", "");
        return Spaces().Replace(plain.Replace(' ', ' '), " ").Trim();
    }

    [GeneratedRegex(@"(?<!\d)(?<b>\d{2,4})\s*[xX×*]\s*(?<h>\d{2,4})(?!\d)")]
    private static partial Regex Size();

    // D1, DC2, DP12A, DK-3, B1, GB2', DM4.1: a few letters then a number.
    [GeneratedRegex(@"^[A-Za-zĐđ]{1,4}\s?-?\d{1,3}[A-Za-z0-9.'\-]{0,6}$")]
    private static partial Regex NameOnly();

    [GeneratedRegex(@"\\[A-Za-z][^;\\{}]*;")]
    private static partial Regex Formatting();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
