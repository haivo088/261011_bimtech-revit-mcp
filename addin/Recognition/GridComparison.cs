namespace Bimtech.RevitMcp.Recognition;

// Recognized grids checked against the truth: same direction and coordinate within ToleranceMm is a match;
// a match with another name counts as a wrong name. Missing = in the truth only; Extra = recognized only.
public sealed record GridComparison(
    int TruthCount, int RecognizedCount, int Matched, IReadOnlyList<string> WrongNames, IReadOnlyList<string> Missing,
    IReadOnlyList<string> Extra)
{
    public const double ToleranceMm = 50;

    public bool IsPerfect => WrongNames.Count == 0 && Missing.Count == 0 && Extra.Count == 0;

    public static GridComparison Compare(IReadOnlyList<RecognizedGrid> recognized, IReadOnlyList<TruthGrid> truth)
    {
        var unmatched = recognized.ToList();
        var wrongNames = new List<string>();
        var missing = new List<string>();
        var matched = 0;
        foreach (var grid in truth)
        {
            var vertical = Math.Abs(grid.X1 - grid.X0) < Math.Abs(grid.Y1 - grid.Y0);
            var coordinate = vertical ? (grid.X0 + grid.X1) / 2 : (grid.Y0 + grid.Y1) / 2;
            var match = unmatched.FirstOrDefault(candidate => candidate.Vertical == vertical
                && Math.Abs((vertical ? candidate.X0 : candidate.Y0) - coordinate) <= ToleranceMm);
            if (match is null)
            {
                missing.Add(grid.Name);
                continue;
            }
            unmatched.Remove(match);
            matched++;
            if (!string.Equals(match.Name, grid.Name, StringComparison.OrdinalIgnoreCase))
                wrongNames.Add($"{grid.Name} (đọc ra \"{match.Name}\")");
        }
        return new GridComparison(truth.Count, recognized.Count, matched, wrongNames, missing,
            unmatched.Select(grid => grid.Name).ToList());
    }
}
