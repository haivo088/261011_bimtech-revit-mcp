using Bimtech.RevitMcp.Recognition;
using Xunit;

namespace Bimtech.RevitMcp.Recognition.Tests;

public sealed class CadFingerprintTests
{
    // A foundation plan: four D400 piles in a 1500x1500 cap, two 300x500 columns, a 220-wide beam drawn as two
    // lines, and texts.
    private static CadCase FoundationPlan() => new()
    {
        Styles =
        [
            new CadStyle { Layer = "COC", LinePattern = "Solid", Color = "#FF0000" },
            new CadStyle { Layer = "DAI", LinePattern = "Solid" },
            new CadStyle { Layer = "COT", LinePattern = "Solid" },
            new CadStyle { Layer = "GIANG", LinePattern = "Hidden" },
        ],
        Arcs =
        [
            new CadArc { Style = 0, Cx = 400, Cy = 400, R = 200, IsCircle = true },
            new CadArc { Style = 0, Cx = 1100, Cy = 400, R = 200, IsCircle = true },
            new CadArc { Style = 0, Cx = 400, Cy = 1100, R = 200, IsCircle = true },
            new CadArc { Style = 0, Cx = 1100, Cy = 1100, R = 200, IsCircle = true },
        ],
        Polylines =
        [
            new CadPolyline { Style = 1, Closed = true, Points = [[0, 0], [1500, 0], [1500, 1500], [0, 1500]] },
            new CadPolyline { Style = 2, Closed = true, Points = [[600, 500], [900, 500], [900, 1000], [600, 1000]] },
            new CadPolyline { Style = 2, Closed = true, Points = [[5600, 500], [5900, 500], [5900, 1000], [5600, 1000]] },
        ],
        Lines =
        [
            new CadLine { Style = 3, X0 = 1500, Y0 = 640, X1 = 5600, Y1 = 640 },
            new CadLine { Style = 3, X0 = 1500, Y0 = 860, X1 = 5600, Y1 = 860 },
        ],
        Texts =
        [
            new CadText { Text = "Đ1" }, new CadText { Text = "Đ1" }, new CadText { Text = "C1 300x500" },
        ],
    };

    [Fact]
    public void Each_layer_reports_its_typical_sizes()
    {
        var fingerprint = CadFingerprintBuilder.Build(FoundationPlan());
        var layers = fingerprint.Layers.ToDictionary(layer => layer.Layer);

        Assert.Equal(4, layers["COC"].Circles);
        Assert.Equal([new SizeCount("400", 4)], layers["COC"].CircleDiametersMm);
        Assert.Equal([new SizeCount("1500x1500", 1)], layers["DAI"].RectanglesMm);
        Assert.Equal([new SizeCount("300x500", 2)], layers["COT"].RectanglesMm);
        Assert.Equal([new SizeCount("220", 1)], layers["GIANG"].ParallelPairWidthsMm);
        Assert.Equal(["Hidden"], layers["GIANG"].LinePatterns);
        Assert.Equal(1, layers["GIANG"].AxisAlignedShare);
    }

    [Fact]
    public void Totals_extent_and_frequent_texts_describe_the_whole_drawing()
    {
        var fingerprint = CadFingerprintBuilder.Build(FoundationPlan());

        Assert.Equal(new FingerprintTotals(2, 0, 4, 3, 3, 3), fingerprint.Totals);
        Assert.Equal(new Extent(0, 0, 5900, 1500), fingerprint.ExtentMm);
        Assert.Equal(new TextSample("Đ1", 2), fingerprint.Texts[0]);
    }
}
