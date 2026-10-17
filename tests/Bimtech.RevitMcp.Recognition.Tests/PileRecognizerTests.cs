using Bimtech.RevitMcp.Recognition;
using Xunit;

namespace Bimtech.RevitMcp.Recognition.Tests;

// Piles and caps are weighed on evidence from every layer, never picked by layer name alone.
public sealed class PileRecognizerTests
{
    // Grids 1, 2 at x = 0, 6000 and A at y = 0, drawn from -1000 to 7000 / 7000.
    private static readonly List<TruthGrid> Grids =
    [
        new() { Name = "1", X0 = 0, Y0 = -1000, X1 = 0, Y1 = 7000 },
        new() { Name = "2", X0 = 6000, Y0 = -1000, X1 = 6000, Y1 = 7000 },
        new() { Name = "A", X0 = -1000, Y0 = 0, X1 = 7000, Y1 = 0 },
    ];

    private static CadCase Scene() => new() { Styles = [new CadStyle { Layer = "L1" }, new CadStyle { Layer = "TRUC" }] };

    private static void Cap(CadCase scene, double cx, double cy, double half = 1000) => scene.Polylines.Add(new CadPolyline
    {
        Closed = true,
        Points = [[cx - half, cy - half], [cx + half, cy - half], [cx + half, cy + half], [cx - half, cy + half]],
    });

    private static void Pile(CadCase scene, double x, double y, double diameter = 600, int style = 0) =>
        scene.Arcs.Add(new CadArc { Style = style, Cx = x, Cy = y, R = diameter / 2, IsCircle = true });

    // A cap of four piles at each crossing A-1 and A-2.
    private static CadCase TwoCaps()
    {
        var scene = Scene();
        foreach (var x in new[] { 0.0, 6000 })
        {
            Cap(scene, x, 0);
            foreach (var (dx, dy) in new[] { (-500.0, -500.0), (500.0, -500.0), (500.0, 500.0), (-500.0, 500.0) }) Pile(scene, x + dx, dy);
        }
        return scene;
    }

    [Fact]
    public void Caps_with_piles_at_crossings_are_sure_on_any_layer()
    {
        var result = PileRecognizer.Recognize(TwoCaps(), Grids, null);

        Assert.Equal(2, result.Caps.Count);
        Assert.All(result.Caps, cap => Assert.True(cap.Sure));
        Assert.Equal("A-1", result.Caps.OrderBy(c => c.Cx).First().Grid);
        Assert.All(result.Caps, cap => Assert.Equal(4, cap.Piles));
        Assert.Equal(8, result.Piles.Count);
        Assert.All(result.Piles, pile =>
        {
            Assert.True(pile.Sure);
            Assert.NotNull(pile.Cap);
            Assert.Contains("+2 " + PileRecognizer.InCapReason, pile.Reasons);
        });
    }

    [Fact]
    public void Grid_bubbles_are_never_piles()
    {
        var scene = TwoCaps();
        // Bubbles of 800 mm at the top ends of grids 1 and 2, on the grid layer.
        Pile(scene, 0, 7400, 800, style: 1);
        Pile(scene, 6000, 7400, 800, style: 1);
        scene.Texts.Add(new CadText { Text = "1", X = 0, Y = 7400 });

        var result = PileRecognizer.Recognize(scene, Grids, null);

        Assert.Equal(8, result.Piles.Count);
        Assert.Equal(2, result.DroppedPiles.Count);
        Assert.All(result.DroppedPiles, pile => Assert.Contains("-4 " + PileRecognizer.BubbleReason, pile.Reasons));
    }

    [Fact]
    public void An_outline_without_piles_is_not_a_sure_cap()
    {
        var scene = TwoCaps();
        Cap(scene, 3000, 4000, 700);

        var result = PileRecognizer.Recognize(scene, Grids, null);

        var empty = result.Caps.Concat(result.DroppedCaps).Single(cap => cap.Cx == 3000);
        Assert.False(empty.Sure);
        Assert.Contains("-2 " + PileRecognizer.NoPileReason, empty.Reasons);
    }

    [Fact]
    public void An_outline_holding_whole_caps_is_not_a_cap()
    {
        var scene = TwoCaps();
        Cap(scene, 3000, 0, 4500);

        var result = PileRecognizer.Recognize(scene, Grids, null);

        var frame = result.Caps.Concat(result.DroppedCaps).Single(cap => cap.Length > 8000);
        Assert.False(frame.Sure);
        Assert.Contains("-3 " + PileRecognizer.HoldsCapsReason, frame.Reasons);
        Assert.All(result.Piles, pile => Assert.True(pile.Cap!.Length < 3000));
    }

    [Fact]
    public void Concentric_circles_are_one_pile_with_its_symbol()
    {
        var scene = Scene();
        Pile(scene, 3000, 3000, 600);
        Pile(scene, 3000, 3000, 100);

        var result = PileRecognizer.Recognize(scene, Grids, null);

        var pile = Assert.Single(result.Piles.Concat(result.DroppedPiles));
        Assert.Equal(600, pile.DiameterMm);
        Assert.True(pile.HasSymbol);
    }

    [Fact]
    public void A_cap_mark_is_read_and_counted()
    {
        var scene = TwoCaps();
        scene.Texts.Add(new CadText { Text = "Đ1 (2000x2000x1200)", X = 0, Y = 0 });

        var result = PileRecognizer.Recognize(scene, Grids, null);

        var cap = result.Caps.OrderBy(c => c.Cx).First();
        Assert.Equal("Đ1", cap.Tag?.Name);
        Assert.Equal(1200, cap.Tag?.ThicknessMm);
        Assert.Contains("+1 " + PileRecognizer.CapTagReason, cap.Reasons);
    }
}
