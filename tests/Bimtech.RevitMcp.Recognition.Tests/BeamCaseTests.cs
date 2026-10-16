using Bimtech.RevitMcp.Recognition;
using Xunit;

namespace Bimtech.RevitMcp.Recognition.Tests;

public sealed class BeamCaseTests
{
    // A 2×2 bay frame in its own (u, v) coordinates: grids 1-3 at u = 0, 6000, 12000; A-B at v = 0, 6000.
    // Columns 300×300 at every crossing, main beams 220 wide on every grid between columns, a mark on grid A.
    // Everything is then rotated by `degrees` into model coordinates, as a drawing on a skewed site would be.
    private static CadCase Case(double degrees, bool withTruth = true)
    {
        var t = degrees * Math.PI / 180;
        (double X, double Y) M(double u, double v) => (u * Math.Cos(t) - v * Math.Sin(t), u * Math.Sin(t) + v * Math.Cos(t));
        var data = new CadCase { Styles = [new CadStyle { Layer = "DAM" }], Model = new CaseModel { Level = "Tầng 2" }, Truth = new CadTruth() };

        void Line(double u0, double v0, double u1, double v1)
        {
            var (a, b) = (M(u0, v0), M(u1, v1));
            data.Lines.Add(new CadLine { X0 = a.X, Y0 = a.Y, X1 = b.X, Y1 = b.Y });
        }
        double[] us = [0, 6000, 12000], vs = [0, 6000];
        foreach (var (u, name) in us.Zip(["1", "2", "3"]))
        {
            var (a, b) = (M(u, -2000), M(u, 8000));
            data.Truth.Grids.Add(new TruthGrid { Name = name, X0 = a.X, Y0 = a.Y, X1 = b.X, Y1 = b.Y });
        }
        foreach (var (v, name) in vs.Zip(["A", "B"]))
        {
            var (a, b) = (M(-2000, v), M(14000, v));
            data.Truth.Grids.Add(new TruthGrid { Name = name, X0 = a.X, Y0 = a.Y, X1 = b.X, Y1 = b.Y });
        }
        foreach (var u in us)
        foreach (var v in vs)
        {
            var c = M(u, v);
            data.Model.Supports.Add(new CaseSupport
            {
                Cx = c.X, Cy = c.Y,
                Points = new[] { M(u - 150, v - 150), M(u + 150, v - 150), M(u + 150, v + 150), M(u - 150, v + 150) }
                    .Select(p => new[] { p.X, p.Y }).ToList(),
            });
        }
        void Beam(double u0, double v0, double u1, double v1)
        {
            var alongU = v0 == v1;
            var (from, to) = alongU ? (u0 + 150, u1 - 150) : (v0 + 150, v1 - 150);
            foreach (var side in new[] { -110.0, 110 })
                if (alongU) Line(from, v0 + side, to, v0 + side);
                else Line(u0 + side, from, u0 + side, to);
            var (a, b) = (M(u0, v0), M(u1, v1));
            if (withTruth) data.Truth.Beams.Add(new TruthBeam { X0 = a.X, Y0 = a.Y, X1 = b.X, Y1 = b.Y });
        }
        foreach (var v in vs) { Beam(0, v, 6000, v); Beam(6000, v, 12000, v); }
        foreach (var u in us) Beam(u, 0, u, 6000);

        var mark = M(3000, 300);
        data.Texts.Add(new CadText { Text = "D1 (220x600)", X = mark.X, Y = mark.Y, RotationDeg = degrees });
        return data;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(-12.5)]
    public void A_case_replays_beam_recognition_in_the_frame_of_its_grids(double degrees)
    {
        var result = BeamCase.Run(Case(degrees));

        Assert.True(result.IsPerfect, string.Join("; ", result.Missing.Concat(result.Extra)));
        Assert.Equal(7, result.Matched);
        Assert.Contains(result.Beams, beam => beam.Span is "A-1 → A-2" or "A-2 → A-1");
        var marked = result.Beams.Where(beam => beam.Beam.Tag?.Name == "D1").ToList();
        Assert.Equal(2, marked.Count); // the marked span, and the one in line through column A-2
    }

    [Fact]
    public void Missing_and_extra_beams_are_named()
    {
        var data = Case(0);
        data.Truth!.Beams.Add(new TruthBeam { Mark = "D9", X0 = 0, Y0 = 3000, X1 = 6000, Y1 = 3000 });
        data.Truth.Beams.RemoveAt(0);

        var result = BeamCase.Run(data);

        Assert.Equal(["D9 (0, 3000) → (6000, 3000)"], result.Missing);
        Assert.Single(result.Extra);
        Assert.Contains("A-1", result.Extra[0]);
    }

    [Fact]
    public void The_frame_follows_the_grids_and_sorts_them_into_two_families()
    {
        var frame = BeamCase.FrameOf(Case(30).Truth!.Grids);

        Assert.Equal(30, frame.ThetaRad * 180 / Math.PI, 6);
        Assert.Equal(["A", "B"], frame.AlongU.Select(axis => axis.Name));
        Assert.Equal(["1", "2", "3"], frame.AlongV.Select(axis => axis.Name));
        Assert.Equal(6000, frame.AlongU[1].PositionMm, 6);
    }

    [Fact]
    public void A_case_with_its_model_and_beams_survives_save_and_load()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bimtech-beam-case-{Guid.NewGuid():N}.json");
        try
        {
            Case(30).Save(path);
            var loaded = CadCase.Load(path);

            Assert.Equal("Tầng 2", loaded.Model?.Level);
            Assert.Equal(6, loaded.Model!.Supports.Count);
            Assert.Equal(7, loaded.Truth!.Beams.Count);
            Assert.True(BeamCase.Run(loaded).IsPerfect);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
