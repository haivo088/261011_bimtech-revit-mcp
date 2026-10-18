using System.Text.Json;

namespace Bimtech.RevitMcp.Ui;

// A point of a preview drawing, in model millimetres.
public readonly record struct PointMm(double X, double Y);

// One polyline of a preview drawing: a beam or wall axis (open) or a cap, column or pile outline (closed).
public sealed record PreviewShape(IReadOnlyList<PointMm> Points, bool Closed);

// The shapes of one plan item and the level it goes on (null for grids, or when the item names no level).
public sealed record PreviewGroup(string? LevelId, IReadOnlyList<PreviewShape> Shapes)
{
    public static readonly PreviewGroup None = new(null, []);
}

// The plan view drawing of an item that does not exist in the model yet, so the reviewer can see where it will land.
// Reads the same params the create_* actions write from (mm, model coordinates). Uses no Revit types, so it is unit tested;
// PreviewServer draws the result. A missing or malformed field draws nothing for that part instead of failing.
public static class PreviewShapes
{
    // Segments of a pile circle. Enough to read as round at any zoom a reviewer uses.
    public const int CircleSegments = 24;

    public static IReadOnlyList<PreviewShape> From(string action, JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return [];
        return action switch
        {
            "create_columns" => Columns(parameters).ToList(),
            "create_beams" => Beams(parameters).ToList(),
            "create_walls" => Walls(parameters).ToList(),
            "create_pile_cap" => PileCap(parameters).ToList(),
            "create_floor" => Polygon(parameters, "points") is { } floor ? [floor] : [],
            "create_grid" => Grid(parameters) is { } grid ? [grid] : [],
            _ => [],
        };
    }

    // The level the item is placed on, for drawing it at the right height in 3D views. Null for grids.
    public static string? LevelId(JsonElement parameters) =>
        parameters.ValueKind != JsonValueKind.Object ? null
            : new[] { "capLevelId", "levelId", "baseLevelId" }.Select(name => Text(parameters, name)).FirstOrDefault(id => id is not null);

    // The rectangle around all shapes, or null when there are none.
    public static (PointMm Min, PointMm Max)? Extent(IEnumerable<PreviewShape> shapes)
    {
        var points = shapes.SelectMany(shape => shape.Points).ToList();
        if (points.Count == 0) return null;
        return (new PointMm(points.Min(p => p.X), points.Min(p => p.Y)), new PointMm(points.Max(p => p.X), points.Max(p => p.Y)));
    }

    // Each column is a rectangle: lengthMm along angleDeg, widthMm across it, centred on (x, y). Same as ColumnPlacer.
    private static IEnumerable<PreviewShape> Columns(JsonElement parameters)
    {
        foreach (var column in Items(parameters, "columns"))
        {
            if (Number(column, "x") is not { } x || Number(column, "y") is not { } y
                || Number(column, "lengthMm") is not { } length || Number(column, "widthMm") is not { } width) continue;
            var angle = (Number(column, "angleDeg") ?? 0) * Math.PI / 180;
            var (cos, sin) = (Math.Cos(angle), Math.Sin(angle));
            var (along, across) = ((X: cos * length / 2, Y: sin * length / 2), (X: -sin * width / 2, Y: cos * width / 2));
            yield return new PreviewShape(
            [
                new(x - along.X - across.X, y - along.Y - across.Y),
                new(x + along.X - across.X, y + along.Y - across.Y),
                new(x + along.X + across.X, y + along.Y + across.Y),
                new(x - along.X + across.X, y - along.Y + across.Y),
            ], Closed: true);
        }
    }

    private static IEnumerable<PreviewShape> Beams(JsonElement parameters)
    {
        foreach (var beam in Items(parameters, "beams"))
            if (Number(beam, "x0") is { } x0 && Number(beam, "y0") is { } y0 && Number(beam, "x1") is { } x1 && Number(beam, "y1") is { } y1)
                yield return new PreviewShape([new(x0, y0), new(x1, y1)], Closed: false);
    }

    private static IEnumerable<PreviewShape> Walls(JsonElement parameters)
    {
        foreach (var line in Items(parameters, "lines"))
            if (Numbers(line) is [var x0, var y0, var x1, var y1])
                yield return new PreviewShape([new(x0, y0), new(x1, y1)], Closed: false);
    }

    private static IEnumerable<PreviewShape> PileCap(JsonElement parameters)
    {
        if (Polygon(parameters, "outline") is { } outline) yield return outline;
        foreach (var pile in Items(parameters, "piles"))
            if (Number(pile, "x") is { } x && Number(pile, "y") is { } y && Number(pile, "diameterMm") is { } diameter && diameter > 0)
                yield return Circle(new PointMm(x, y), diameter / 2);
    }

    private static PreviewShape? Grid(JsonElement parameters) =>
        Point(parameters, "startMm") is { } start && Point(parameters, "endMm") is { } end
            ? new PreviewShape([start, end], Closed: false)
            : null;

    // [[x, y], ...] with at least three points.
    private static PreviewShape? Polygon(JsonElement parameters, string name)
    {
        var points = Items(parameters, name).Select(Numbers).Where(pair => pair.Length == 2).Select(pair => new PointMm(pair[0], pair[1])).ToList();
        return points.Count >= 3 ? new PreviewShape(points, Closed: true) : null;
    }

    private static PreviewShape Circle(PointMm center, double radius) =>
        new(Enumerable.Range(0, CircleSegments)
            .Select(i => 2 * Math.PI * i / CircleSegments)
            .Select(angle => new PointMm(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle)))
            .ToList(), Closed: true);

    private static PointMm? Point(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var point) && point.ValueKind == JsonValueKind.Object
            && Number(point, "x") is { } x && Number(point, "y") is { } y
            ? new PointMm(x, y)
            : null;

    private static IEnumerable<JsonElement> Items(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : [];

    // All numbers of a JSON array such as [x0, y0, x1, y1]; empty if any entry is not a finite number.
    private static double[] Numbers(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array) return [];
        var values = new List<double>();
        foreach (var value in array.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number)) return [];
            values.Add(number);
        }
        return values.ToArray();
    }

    private static double? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)
            ? number
            : null;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
