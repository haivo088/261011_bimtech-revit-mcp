using Autodesk.Revit.DB;
using View = Autodesk.Revit.DB.View;

namespace Bimtech.StructuralCore.Levels;

/// <summary>Kéo Grid và Level cho khớp nhau: mọi Grid cao từ Level thấp nhất − gridMargin tới Level cao nhất + gridMargin,
/// mọi Level dài ra ngoài trục biên mỗi phương levelMargin. Phạm vi 3D của Level đặt trong một mặt đứng mỗi phương
/// (North/South theo trục đứng, East/West theo trục ngang); phạm vi 2D của Level và Grid ở mọi mặt đứng theo 3D.
/// Chạy lại bao nhiêu lần cũng ra cùng kết quả. Chạy trong Transaction do caller mở sẵn.</summary>
internal static class DatumExtents
{
    internal sealed record Result(
        int Grids, int Levels, double BottomMm, double TopMm, List<string> Views, List<string> Problems);

    private const double AxisTolerance = 0.99;

    public static Result Fit(Document doc, double gridMarginMm, double levelMarginMm)
    {
        doc.Regenerate();
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
        var grids = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>()
            .Where(grid => grid.Curve is Line).ToList();
        var problems = new List<string>();
        if (levels.Count == 0 || grids.Count == 0)
            return new Result(0, 0, 0, 0, [], ["Model chưa có Level hoặc Grid: không có gì để căn."]);

        var gridMargin = Feet(gridMarginMm);
        var levelMargin = Feet(levelMarginMm);
        var bottom = levels.Min(level => level.Elevation) - gridMargin;
        var top = levels.Max(level => level.Elevation) + gridMargin;

        var fittedGrids = 0;
        foreach (var grid in grids)
        {
            try
            {
                grid.SetVerticalExtents(bottom, top);
                fittedGrids++;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException exception)
            {
                problems.Add($"Trục {grid.Name}: {exception.Message}");
            }
        }

        // Vertical grids give the X range of the levels, horizontal grids the Y range. Skewed grids give neither.
        var lines = grids.Select(grid => (Line)grid.Curve).ToList();
        var xs = lines.Where(line => Math.Abs(line.Direction.Y) > AxisTolerance).Select(line => line.Origin.X).ToList();
        var ys = lines.Where(line => Math.Abs(line.Direction.X) > AxisTolerance).Select(line => line.Origin.Y).ToList();

        var elevations = new FilteredElementCollector(doc).OfClass(typeof(ViewSection)).Cast<View>()
            .Where(view => !view.IsTemplate && view.ViewType == ViewType.Elevation)
            .ToList();
        var fittedLevels = new HashSet<ElementId>();
        var axesDone = new HashSet<bool>();
        var views = new List<string>();
        foreach (var view in elevations)
        {
            var right = view.RightDirection;
            bool? alongX = Math.Abs(right.X) > AxisTolerance ? true : Math.Abs(right.Y) > AxisTolerance ? false : null;
            if (alongX is not { } isX) continue;
            var positions = isX ? xs : ys;
            if (positions.Count == 0) continue;

            // 3D extents are shared by every view of one direction: set them once, then make each view follow them.
            var setModel = axesDone.Add(isX);
            var (from, to) = (positions.Min() - levelMargin, positions.Max() + levelMargin);
            foreach (var level in levels.Where(level => level.CanBeVisibleInView(view)))
            {
                try
                {
                    if (setModel && level.GetCurvesInView(DatumExtentType.Model, view).FirstOrDefault() is Line curve)
                    {
                        level.SetCurveInView(DatumExtentType.Model, view, Span(curve, isX, from, to));
                        fittedLevels.Add(level.Id);
                    }
                    FollowModel(level, view);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException exception)
                {
                    problems.Add($"Level {level.Name} trong {view.Name}: {exception.Message}");
                }
            }
            foreach (var grid in grids.Where(grid => grid.CanBeVisibleInView(view)))
            {
                try
                {
                    FollowModel(grid, view);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException exception)
                {
                    problems.Add($"Trục {grid.Name} trong {view.Name}: {exception.Message}");
                }
            }
            views.Add(view.Name);
        }
        if (views.Count == 0) problems.Add("Model không có mặt đứng vuông góc với trục: chưa kéo được Level.");

        return new Result(fittedGrids, fittedLevels.Count, Mm(bottom), Mm(top), views, problems);
    }

    // The same line, cut to run from..to along X (or Y), keeping its direction.
    private static Line Span(Line curve, bool alongX, double from, double to)
    {
        var start = curve.GetEndPoint(0);
        var direction = curve.Direction;
        var step = alongX ? direction.X : direction.Y;
        var origin = alongX ? start.X : start.Y;
        var a = start + direction * ((from - origin) / step);
        var b = start + direction * ((to - origin) / step);
        return step > 0 ? Line.CreateBound(a, b) : Line.CreateBound(b, a);
    }

    private static void FollowModel(DatumPlane datum, View view)
    {
        datum.SetDatumExtentType(DatumEnds.End0, view, DatumExtentType.Model);
        datum.SetDatumExtentType(DatumEnds.End1, view, DatumExtentType.Model);
    }

    private static double Feet(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);

    private static double Mm(double feet) => Math.Round(UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters), 1);
}
