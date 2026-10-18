namespace Bimtech.RevitMcp.Recognition;

// The confidence of one review item, on the Evidence scale. An item holds a group of elements (columns of one size,
// walls of one thickness, a cap with its piles), so it is as sure as its least sure element. Some items always need a
// look whatever their evidence: one that creates a type, or whose mark and drawing disagree; they stay just below the
// automatic threshold.
public static class ConfidenceRules
{
    // 0..1 for PlanItem.Confidence. No elements weighed: the scale's start.
    public static double Group(IEnumerable<int> percents, bool needsLook = false)
    {
        var lowest = percents.DefaultIfEmpty(Evidence.BasePercent).Min();
        if (needsLook) lowest = Math.Min(lowest, Evidence.AutoFromPercent - 1);
        return Evidence.Confidence(lowest);
    }

    // A main beam: both ends on supports (always: only support-to-support spans are found) +20%, its size read from a
    // mark +10%; a mark borrowed from the beam in line, a mark whose b differs from the drawn width, broken edges and
    // an axis on no grid −10% each.
    public static int MainBeam(bool sizeFromMark, bool inheritedMark, bool widthMismatch, bool broken, bool onGrid) =>
        Evidence.BasePercent + 20
        + (sizeFromMark ? 10 : 0)
        - (inheritedMark ? 10 : 0)
        - (widthMismatch ? 10 : 0)
        - (broken ? 10 : 0)
        - (onGrid ? 0 : 10);
}
