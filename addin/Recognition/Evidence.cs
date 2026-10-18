namespace Bimtech.RevitMcp.Recognition;

// One confidence scale for every recognizer, in percent: a candidate starts at BasePercent, each piece of evidence
// adds or takes away its weight, and the total decides what happens to it:
//   ≥ AutoFromPercent  sure: starts approved in the review window (PlanConfidence.AutoApproveFrom);
//   ≥ DropBelowPercent to review: proposed, not ticked;
//   below              dropped: reported with its reasons, never proposed.
// Weights: strong structural relations ±20 (on a grid crossing, piles inside, a support below, a hatch), supporting
// evidence ±10 (a repeated size, a mark), layer names ±5 (a hint only); exclusions −20 to −40.
public static class Evidence
{
    public const int BasePercent = 50;
    public const int AutoFromPercent = 50;
    public const int DropBelowPercent = 20;
    // Where a veto holds a candidate: to review, never sure.
    public const int VetoCapPercent = 40;

    // "+20% ở giao trục", "-10% đơn lẻ".
    public static string Reason(int percent, string text) => $"{(percent > 0 ? "+" : "")}{percent}% {text}";

    // "loại trừ: thẳng hàng với mảnh cùng bề dày".
    public static string VetoReason(string text) => $"loại trừ: {text}";

    public static bool IsSure(int percent) => percent >= AutoFromPercent;
    public static bool IsKept(int percent) => percent >= DropBelowPercent;

    // 0..1 for PlanItem.Confidence.
    public static double Confidence(int percent) => Math.Clamp(percent, 0, 100) / 100.0;
}

// A candidate weighed on evidence: its percent and its signed reasons for the review window. An exclusion is a veto,
// not a weight: "this is a piece of wall" settles it however much else speaks for a column, so the candidate is held
// at VetoCapPercent (always reviewed, never approved by itself), lower if its evidence says so.
public abstract class Weighed
{
    private int _sum = Evidence.BasePercent;
    private bool _vetoed;
    private int _cap = 100;

    public int Percent => _vetoed ? Math.Min(_sum, _cap) : _sum;
    public bool Sure => Evidence.IsSure(Percent);
    public List<string> Reasons { get; } = [];

    internal void Add(int percent, string reason)
    {
        _sum += percent;
        Reasons.Add(Evidence.Reason(percent, reason));
    }

    // cap: where the veto holds it; VetoCapPercent keeps it to review, below DropBelowPercent drops it.
    internal void Veto(string reason, int cap = Evidence.VetoCapPercent)
    {
        _cap = Math.Min(_cap, cap);
        _vetoed = true;
        Reasons.Add(Evidence.VetoReason(reason));
    }
}
