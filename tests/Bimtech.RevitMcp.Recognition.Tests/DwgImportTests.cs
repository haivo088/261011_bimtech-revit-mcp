using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using Bimtech.RevitMcp.Recognition;
using Xunit;
using CSMath;

namespace Bimtech.RevitMcp.Recognition.Tests;

public class DwgImportTests
{
    private static Hatch Rectangle(double x0, double y0, double x1, double y1)
    {
        var path = new Hatch.BoundaryPath();
        var outline = new Hatch.BoundaryPath.Polyline { IsClosed = true };
        outline.Vertices.AddRange([new XYZ(x0, y0, 0), new XYZ(x1, y0, 0), new XYZ(x1, y1, 0), new XYZ(x0, y1, 0)]);
        path.Edges.Add(outline);
        var hatch = new Hatch { Pattern = HatchPattern.Solid, IsSolid = true };
        hatch.Paths.Add(path);
        return hatch;
    }

    [Fact]
    public void Reads_lines_hatches_and_texts_in_mm()
    {
        var document = new CadDocument();
        document.Header.InsUnits = ACadSharp.Types.Units.UnitsType.Centimeters;
        var walls = new Layer("VACH");
        document.Layers.Add(walls);
        document.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(100, 0, 0), Layer = walls });
        var hatch = Rectangle(0, 0, 500, 30);
        hatch.Layer = walls;
        document.Entities.Add(hatch);
        document.Entities.Add(new TextEntity { Value = " V1 ", InsertPoint = new XYZ(10, 50, 0), Height = 25, Rotation = Math.PI / 2 });

        var data = DwgImport.ToCase(document, "plan.dwg");

        var line = Assert.Single(data.Lines);
        Assert.Equal(1000, line.X1);
        Assert.Equal("VACH", data.Styles[line.Style].Layer);
        Assert.Equal("plan.dwg", data.Styles[line.Style].Category);

        var read = Assert.Single(data.Hatches);
        Assert.True(read.IsSolid);
        Assert.Equal("VACH", data.Styles[read.Style].Layer);
        var loop = Assert.Single(read.Loops);
        Assert.Equal(4, loop.Count);
        Assert.Contains(loop, p => p[0] == 5000 && p[1] == 300);

        var text = Assert.Single(data.Texts);
        Assert.Equal("V1", text.Text);
        Assert.Equal(100, text.X);
        Assert.Equal(500, text.Y);
        Assert.Equal(250, text.HeightMm);
        Assert.Equal(90, text.RotationDeg);
        Assert.NotNull(text.Style);
    }

    [Fact]
    public void Expands_blocks_with_their_placement()
    {
        var document = new CadDocument();
        var block = new BlockRecord("COT");
        block.Entities.Add(Rectangle(-200, -200, 200, 200));
        block.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(100, 0, 0) });
        document.BlockRecords.Add(block);
        document.Entities.Add(new Insert(block) { InsertPoint = new XYZ(5000, 1000, 0), Rotation = Math.PI / 2, XScale = 2, YScale = 2 });

        var data = DwgImport.ToCase(document, "plan.dwg");

        var line = Assert.Single(data.Lines);
        Assert.Equal((5000, 1000, 5000, 1200), (line.X0, line.Y0, line.X1, line.Y1));
        var loop = Assert.Single(Assert.Single(data.Hatches).Loops);
        Assert.Equal(4600, loop.Min(p => p[0]), 6);
        Assert.Equal(5400, loop.Max(p => p[0]), 6);
        Assert.Equal(600, loop.Min(p => p[1]), 6);
    }

    [Fact]
    public void Bulged_polyline_points_follow_the_arc()
    {
        var document = new CadDocument();
        var polyline = new LwPolyline();
        // A semicircle of radius 1000 from (1000, 0) counterclockwise to (-1000, 0), closed by the straight back.
        polyline.Vertices.Add(new LwPolyline.Vertex(new XY(1000, 0)) { Bulge = 1 });
        polyline.Vertices.Add(new LwPolyline.Vertex(new XY(-1000, 0)));
        polyline.IsClosed = true;
        document.Entities.Add(polyline);

        var points = Assert.Single(DwgImport.ToCase(document, "plan.dwg").Polylines).Points;

        Assert.All(points, p => Assert.True(p[1] >= -0.01));
        Assert.Contains(points, p => Math.Abs(p[0]) < 0.01 && Math.Abs(p[1] - 1000) < 0.01);
        Assert.All(points, p => Assert.Equal(1000, Math.Sqrt(p[0] * p[0] + p[1] * p[1]), 1));
    }

    [Fact]
    public void Ellipses_with_equal_axes_are_circles_and_arcs_like_Revit_reports_them()
    {
        var document = new CadDocument();
        // A grid bubble drawn as an ellipse, a half of one, and a true ellipse.
        document.Entities.Add(new Ellipse { Center = new XYZ(0, -1400, 0), MajorAxisEndPoint = new XYZ(400, 0, 0), RadiusRatio = 1, StartParameter = 0, EndParameter = 2 * Math.PI });
        document.Entities.Add(new Ellipse { Center = new XYZ(5000, 0, 0), MajorAxisEndPoint = new XYZ(0, 300, 0), RadiusRatio = 1, StartParameter = 0, EndParameter = Math.PI });
        document.Entities.Add(new Ellipse { Center = new XYZ(0, 0, 0), MajorAxisEndPoint = new XYZ(1000, 0, 0), RadiusRatio = 0.5, StartParameter = 0, EndParameter = 2 * Math.PI });

        var data = DwgImport.ToCase(document, "plan.dwg");

        var bubble = data.Arcs.Single(arc => arc.IsCircle);
        Assert.Equal((0.0, -1400.0, 400.0), (bubble.Cx, bubble.Cy, bubble.R));
        // From the major axis (up) counterclockwise by half a turn: from 90° to 270°.
        var half = data.Arcs.Single(arc => !arc.IsCircle);
        Assert.Equal((300.0, 90.0, 270.0), (half.R, half.StartDeg, half.EndDeg));
        var oval = Assert.Single(data.Polylines);
        Assert.True(oval.Closed);
        Assert.Equal(1000, oval.Points.Max(p => p[0]), 1);
        Assert.Equal(500, oval.Points.Max(p => p[1]), 1);
    }
}
