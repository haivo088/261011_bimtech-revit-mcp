using System.Text.Json;

namespace Bimtech.RevitMcp.Data;

// Reads and validates a wall/1.0 file. Refuses another schema major version, a unit other than mm, missing columns,
// non-positive thickness and zero-length walls, instead of guessing.
public static class WallDataReader
{
    public const string SchemaPrefix = "wall/1.";

    private static readonly string[] RequiredColumns = ["type", "thickness", "x1", "y1", "x2", "y2", "grid_start", "grid_end"];

    public static WallDataSet Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new DataFileException($"Not valid JSON: {exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            var meta = Json.RequireObject(root, "meta");
            var schema = Json.RequireString(meta, "schema", "meta");
            if (!schema.StartsWith(SchemaPrefix, StringComparison.Ordinal))
                throw new DataFileException($"meta.schema is '{schema}'; expected '{SchemaPrefix}x'.");
            var unit = Json.RequireString(meta, "unit", "meta");
            if (unit != "mm")
                throw new DataFileException($"meta.unit is '{unit}'; only 'mm' is supported.");
            var level = meta.TryGetProperty("level", out var lv) && lv.ValueKind == JsonValueKind.Object ? lv : default;

            var source = new WallDataSource(
                schema,
                Json.OptionalString(meta, "tool"),
                Json.OptionalString(meta, "source"),
                Json.OptionalString(meta, "drawing"),
                Json.OptionalString(meta, "src"),
                Json.OptionalString(meta, "src_hash"),
                Json.OptionalString(meta, "origin"),
                Json.OptionalString(meta, "grid_file"),
                Json.OptionalString(meta, "grid_hash"),
                Json.OptionalString(level, "base"),
                Json.OptionalString(level, "top"));
            var checks = root.TryGetProperty("checks", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;
            return new WallDataSet(
                source,
                AttachOpenings(ReadWalls(Json.RequireObject(root, "walls")), root),
                ReadBadOutlines(root),
                (int)(Json.OptionalNumber(checks, "slivers") ?? 0),
                Json.StringList(root, "issues"),
                Json.StringList(root, "decisions"));
        }
    }

    // wall/1.1 openings table: [wall row index, x1, y1, x2, y2, width, sill, head, grid] -> on their walls.
    private static List<DataWall> AttachOpenings(List<DataWall> walls, JsonElement root)
    {
        if (!root.TryGetProperty("openings", out var table) || table.ValueKind != JsonValueKind.Object) return walls;
        var (cols, rows) = Json.Table(table, "openings", ["wall", "x1", "y1", "x2", "y2", "sill", "head"]);
        var byWall = new Dictionary<int, List<DataOpening>>();
        var index = 0;
        foreach (var row in rows)
        {
            var where = $"openings.rows[{index++}]";
            double? N(string name) => row[cols.IndexOf(name)].ValueKind == JsonValueKind.Number ? row[cols.IndexOf(name)].GetDouble() : null;
            var wall = (int)(N("wall") ?? throw new DataFileException($"{where}.wall is not a number."));
            if (wall < 0 || wall >= walls.Count)
                throw new DataFileException($"{where}.wall {wall} is not a row of the walls table.");
            var opening = new DataOpening(N("x1") ?? 0, N("y1") ?? 0, N("x2") ?? 0, N("y2") ?? 0, N("sill") ?? 0, N("head"),
                cols.Contains("grid") ? row[cols.IndexOf("grid")].GetString() : null);
            (byWall.TryGetValue(wall, out var list) ? list : byWall[wall] = []).Add(opening);
        }
        return walls.Select((w, i) => byWall.TryGetValue(i, out var ops) ? w with { Openings = ops } : w).ToList();
    }

    // Outline ids whose wall rectangles do not add up to the outline area (outlines table: area_m2 vs rect_area_m2).
    private static HashSet<string> ReadBadOutlines(JsonElement root)
    {
        var bad = new HashSet<string>(StringComparer.Ordinal);
        if (!root.TryGetProperty("outlines", out var table) || table.ValueKind != JsonValueKind.Object) return bad;
        var (cols, rows) = Json.Table(table, "outlines", ["id", "area_m2", "rect_area_m2"]);
        foreach (var row in rows)
        {
            var (id, area, rect) = (row[cols.IndexOf("id")].GetString(), row[cols.IndexOf("area_m2")].GetDouble(),
                row[cols.IndexOf("rect_area_m2")].GetDouble());
            if (id is not null && Math.Abs(area - rect) > 1e-4) bad.Add(id);
        }
        return bad;
    }

    private static List<DataWall> ReadWalls(JsonElement table)
    {
        var (cols, rows) = Json.Table(table, "walls", RequiredColumns);
        int Col(string name) => cols.IndexOf(name);
        var list = new List<DataWall>();
        var index = 0;
        foreach (var row in rows)
        {
            var where = $"walls.rows[{index++}]";
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != cols.Count)
                throw new DataFileException($"{where} does not have {cols.Count} values.");
            string? Text(string name) => Col(name) >= 0 && row[Col(name)].ValueKind == JsonValueKind.String ? row[Col(name)].GetString() : null;
            double Number(string name) => row[Col(name)].ValueKind == JsonValueKind.Number && double.IsFinite(row[Col(name)].GetDouble())
                ? row[Col(name)].GetDouble()
                : throw new DataFileException($"{where}.{name} is not a number.");
            var flags = Col("flags") >= 0 && row[Col("flags")].ValueKind == JsonValueKind.Array
                ? row[Col("flags")].EnumerateArray().Where(f => f.ValueKind == JsonValueKind.String).Select(f => f.GetString()!).ToList()
                : [];

            var (x1, y1, x2, y2, t) = (Number("x1"), Number("y1"), Number("x2"), Number("y2"), Number("thickness"));
            if (t <= 0)
                throw new DataFileException($"{where}.thickness must be positive.");
            if (Math.Abs(x2 - x1) + Math.Abs(y2 - y1) < 1)
                throw new DataFileException($"{where}: the wall has no length.");
            list.Add(new DataWall(Text("type") ?? $"V{t:0}", t, x1, y1, x2, y2, Text("grid_start") ?? "", Text("grid_end") ?? "",
                Text("outline"), flags, []));
        }
        if (list.Count == 0)
            throw new DataFileException("walls.rows is empty.");
        return list;
    }
}
