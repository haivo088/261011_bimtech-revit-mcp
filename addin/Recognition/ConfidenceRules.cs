namespace Bimtech.RevitMcp.Recognition;

// How sure a proposed group of columns, walls or beams is, 0..1. A group gets Sure only when every member passes the
// rule; from PlanConfidence.AutoApproveFrom (0.9) the review window ticks it for the person. Groups that fail get a
// lower score saying how close they came, always below 0.9, so they are never ticked.
public static class ConfidenceRules
{
    public const double Sure = 0.95;
    // The most a group that fails its rule can score.
    private const double Close = 0.85;

    // Columns: every column at a grid crossing, an existing type of exactly b×h (or one the person assigned), and the
    // tag in the drawing agrees with the drawn size. A type created from a template family is checked by eye first.
    public static double Columns(int count, int atCrossings, bool existingType, bool tagMismatch)
    {
        if (count <= 0) return 0;
        if (atCrossings == count && existingType && !tagMismatch) return Sure;
        return Math.Round(Close * atCrossings / count * (existingType ? 1 : 0.9) * (tagMismatch ? 0.8 : 1), 2);
    }

    // Walls: a sure group (WallRecognizer) whose every wall has a hatch, the thickness of a concrete wall and at least
    // three pieces of evidence for it. Null for walls read from line pairs, which carry no evidence.
    public static double? Walls(bool? groupSure, IReadOnlyList<IReadOnlyList<string>> evidencePerWall, bool repaired)
    {
        if (groupSure is null || evidencePerWall.Count == 0) return null;
        if (groupSure == false) return 0.4;
        var passing = evidencePerWall.Count(evidence =>
            Has(evidence, WallRecognizer.HatchReason)
            && Has(evidence, WallRecognizer.ConcreteSizeReason)
            && evidence.Count(IsFor) >= 3);
        if (passing == evidencePerWall.Count && !repaired) return Sure;
        return Math.Round(0.6 + (Close - 0.6) * passing / evidencePerWall.Count * (repaired ? 0.8 : 1), 2);
    }

    // Beams: the size comes from a mark, not from the drawn width or a height the person typed, and it agrees with
    // the drawn width; every beam is well drawn. Both ends sit on a column or wall by construction (BeamRecognizer only
    // keeps support-to-support spans), so supports need no check here.
    public static double Beams(int count, bool sizeFromMark, int inheritedMarks, int widthMismatches, int brokenLines)
    {
        if (count <= 0) return 0;
        if (!sizeFromMark) return 0.5;
        var doubtful = Math.Min(count, inheritedMarks + widthMismatches + brokenLines);
        if (doubtful == 0) return Sure;
        return Math.Round(0.6 + (Close - 0.6) * (count - doubtful) / count, 2);
    }

    // Evidence reads like "+1 có hatch" or "-2 dày cỡ tường xây".
    private static bool IsFor(string evidence) => evidence.StartsWith('+');

    private static bool Has(IReadOnlyList<string> evidence, string reason) =>
        evidence.Any(item => IsFor(item) && item.EndsWith(" " + reason, StringComparison.Ordinal));
}
