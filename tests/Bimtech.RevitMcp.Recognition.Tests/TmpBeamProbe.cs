using System.Globalization;
using Xunit;
using Xunit.Abstractions;
namespace Bimtech.RevitMcp.Recognition.Tests;
public sealed class TmpBeamProbe(ITestOutputHelper output)
{
    private const string Dir = @"C:\Users\Admin\AppData\Local\Temp\claude\42e62d6eda4400e4\1b5c7c08-cb47-466d-aeb2-ad721d0f4aef\scratchpad\";
    private static double N(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    [Fact]
    public void Probe()
    {
        var lines = File.ReadAllLines(Dir + "revit_lines.tsv").Select(l => l.Split('\t'))
            .Select(f => new BeamRecognizer.Line(N(f[1]), N(f[2]), N(f[3]), N(f[4]), Axis: f[0] == "kata_grid")).ToList();
        var sups = File.ReadAllLines(Dir + "revit_supports.tsv").Select(l => l.Split('\t'))
            .Select(f => new BeamRecognizer.Support(f[0], N(f[2]), N(f[3]), N(f[4]), N(f[5]), N(f[9]), N(f[10]), bool.Parse(f[6]), f[1],
                f[7].Length == 0 ? null : bool.Parse(f[7]), f[8].Length == 0 ? null : N(f[8]))).ToList();
        foreach (var digits in new[] { -1, 1 })
        {
            double R(double v) => digits < 0 ? v : Math.Round(v, digits);
            var input = lines.Select(l => l with { U0 = R(l.U0), V0 = R(l.V0), U1 = R(l.U1), V1 = R(l.V1) }).ToList();
            var r = BeamRecognizer.Recognize(input, sups, [], new BeamRecognizer.Options { Secondary = true });
            output.WriteLine($"digits={digits} secondary={r.Beams.Count(b => b.IsSecondary)}");
            foreach (var b in r.Beams.Where(b => b.IsSecondary)) output.WriteLine($"  {b.From.Label} -> {b.To.Label} alongU={b.AlongU} cross={b.Cross:0.###} {b.Start:0}..{b.End:0}");
        }
        var y3 = sups.Where(s => s.Label == "dầm D2-Y3" && s.MinU < 13400 && s.MaxU > 6500 && s.MinV < 10500 && s.MaxV > 9500).ToList();
        var few = lines.Where(l => Math.Abs(l.V0 - l.V1) < 2 && l.V0 > 9800 && l.V0 < 10200 && Math.Min(l.U0, l.U1) < 8000 && Math.Max(l.U0, l.U1) > 12000).ToList();
        output.WriteLine($"MINI raw={BeamRecognizer.Recognize(few, y3, [], new BeamRecognizer.Options { Secondary = true }).Beams.Count}");
        output.WriteLine($"MINI allsups={BeamRecognizer.Recognize(few, sups, [], new BeamRecognizer.Options { Secondary = true }).Beams.Count}");
        // Lines near the beam band: which extra line breaks it?
        var band = lines.Where(l => Math.Max(l.V0, l.V1) > 5800 && Math.Min(l.V0, l.V1) < 14200 && Math.Max(l.U0, l.U1) > 6500 && Math.Min(l.U0, l.U1) < 13400).ToList();
        output.WriteLine($"MINI band={BeamRecognizer.Recognize(band, y3, [], new BeamRecognizer.Options { Secondary = true }).Beams.Count} lines={band.Count}");
        int X7(List<BeamRecognizer.Line> ls, List<BeamRecognizer.Support> ss) => BeamRecognizer.Recognize(ls, ss, [], new BeamRecognizer.Options { Secondary = true })
            .Beams.Count(b => b.AlongU && Math.Abs(b.Cross - 10000) < 50 && Math.Min(b.Start, b.End) < 7000 && Math.Max(b.Start, b.End) > 13000);
        foreach (var b in BeamRecognizer.Recognize(lines, y3, [], new BeamRecognizer.Options { Secondary = true }).Beams)
            output.WriteLine($"RESULT {b.From.Label} -> {b.To.Label} alongU={b.AlongU} cross={b.Cross:R} {b.Start:R}..{b.End:R}");
        output.WriteLine($"X7 all/all={X7(lines, sups)} all/y3={X7(lines, y3)} band/all={X7(band, sups)}");
        // Halve the lines until the breaker is found.
        var suspects = lines.Except(few).ToList();
        while (suspects.Count > 1)
        {
            var half = suspects.Take(suspects.Count / 2).ToList();
            suspects = X7([.. few, .. half], y3) == 0 ? half : suspects.Skip(half.Count).ToList();
        }
        if (suspects.Count == 1 && X7([.. few, suspects[0]], y3) == 0) output.WriteLine($"BREAKER ({suspects[0].U0:R},{suspects[0].V0:R})-({suspects[0].U1:R},{suspects[0].V1:R})");
        foreach (var extra in band.Except(few).Distinct())
        {
            var n = BeamRecognizer.Recognize([.. few, extra], y3, [], new BeamRecognizer.Options { Secondary = true }).Beams.Count;
            if (n == 0) output.WriteLine($"BREAKER ({extra.U0:R},{extra.V0:R})-({extra.U1:R},{extra.V1:R})");
        }
        foreach (var l in lines.Where(l => Math.Abs(l.V0 - l.V1) < 2 && l.V0 > 9800 && l.V0 < 10200 && Math.Min(l.U0, l.U1) < 8000 && Math.Max(l.U0, l.U1) > 12000))
            output.WriteLine($"EDGE ({l.U0:R},{l.V0:R})-({l.U1:R},{l.V1:R})");
        foreach (var s in sups.Where(s => s.MinU < 13400 && s.MaxU > 6500 && s.MinV < 10500 && s.MaxV > 9500))
            output.WriteLine($"SUP {s.Label} u {s.MinU:R}..{s.MaxU:R} v {s.MinV:R}..{s.MaxV:R}");
    }
}
