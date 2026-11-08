using System.Text.Json;

namespace Bimtech.RevitMcp.Data;

// Reads and validates a slab/1.0 file. Refuses another schema major version, a unit other than mm, missing columns,
// a non-positive thickness and outlines with fewer than 3 points, instead of guessing.
public static class SlabDataReader
{
    public const string SchemaPrefix = "slab/1.";

    public static SlabDataSet Parse(string json)
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

            var source = new SlabDataSource(
                schema,
                Json.OptionalString(meta, "tool"),
                Json.OptionalString(meta, "source"),
                Json.OptionalString(meta, "drawing"),
                Json.OptionalString(meta, "src"),
                Json.OptionalString(meta, "src_hash"),
                Json.OptionalString(meta, "origin"),
                Json.OptionalString(meta, "grid_file"),
                Json.OptionalString(meta, "grid_hash"),
                Json.OptionalString(level, "base"));
            var slabs = ReadSlabs(Json.RequireObject(root, "slabs"));
            var openings = root.TryGetProperty("openings", out var o) && o.ValueKind == JsonValueKind.Object ? ReadOpenings(o) : [];
            return new SlabDataSet(source, slabs, openings, Json.StringList(root, "issues"), Json.StringList(root, "decisions"));
        }
    }

    private static List<DataSlab> ReadSlabs(JsonElement table)
    {
        var (cols, rows) = Json.Table(table, "slabs", ["id", "thickness", "points"]);
        int Col(string name) => cols.IndexOf(name);
        var list = new List<DataSlab>();
        var index = 0;
        foreach (var row in rows)
        {
            var where = $"slabs.rows[{index++}]";
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != cols.Count)
                throw new DataFileException($"{where} does not have {cols.Count} values.");
            string? Text(string name) => Col(name) >= 0 && row[Col(name)].ValueKind == JsonValueKind.String ? row[Col(name)].GetString() : null;
            double? Number(string name) => Col(name) >= 0 && row[Col(name)].ValueKind == JsonValueKind.Number ? row[Col(name)].GetDouble() : null;
            var thickness = Number("thickness") ?? throw new DataFileException($"{where}.thickness is not a number (no slab thickness in _fix.json?).");
            if (thickness <= 0)
                throw new DataFileException($"{where}.thickness must be positive.");
            var ids = Col("openings") >= 0 && row[Col("openings")].ValueKind == JsonValueKind.Array
                ? row[Col("openings")].EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList()
                : [];
            var holes = Col("holes") >= 0 && row[Col("holes")].ValueKind == JsonValueKind.Array
                ? row[Col("holes")].EnumerateArray().Select((h, i) => (IReadOnlyList<(double X, double Y)>)ReadPoints(h, $"{where}.holes[{i}]")).ToList()
                : null;
            list.Add(new DataSlab(Text("id") ?? $"S{index}", Text("zone") ?? "main", thickness, Number("top_offset") ?? 0,
                ReadPoints(row[Col("points")], $"{where}.points"), ids, Text("grid_ref") ?? "", holes));
        }
        if (list.Count == 0)
            throw new DataFileException("slabs.rows is empty.");
        return list;
    }

    private static List<DataSlabOpening> ReadOpenings(JsonElement table)
    {
        var (cols, rows) = Json.Table(table, "openings", ["id", "points"]);
        int Col(string name) => cols.IndexOf(name);
        var list = new List<DataSlabOpening>();
        var index = 0;
        foreach (var row in rows)
        {
            var where = $"openings.rows[{index++}]";
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != cols.Count)
                throw new DataFileException($"{where} does not have {cols.Count} values.");
            string? Text(string name) => Col(name) >= 0 && row[Col(name)].ValueKind == JsonValueKind.String ? row[Col(name)].GetString() : null;
            var flags = Col("flags") >= 0 && row[Col("flags")].ValueKind == JsonValueKind.Array
                ? row[Col("flags")].EnumerateArray().Where(f => f.ValueKind == JsonValueKind.String).Select(f => f.GetString()!).ToList()
                : [];
            var area = Col("area_m2") >= 0 && row[Col("area_m2")].ValueKind == JsonValueKind.Number ? row[Col("area_m2")].GetDouble() : 0;
            list.Add(new DataSlabOpening(Text("id") ?? $"L{index}", Text("slab"), Text("kind") ?? "stair", area,
                ReadPoints(row[Col("points")], $"{where}.points"), Text("grid_ref") ?? "", flags));
        }
        return list;
    }

    private static List<(double X, double Y)> ReadPoints(JsonElement value, string where)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw new DataFileException($"{where} is not a list of [x, y].");
        var points = value.EnumerateArray().Select(p =>
            p.ValueKind == JsonValueKind.Array && p.GetArrayLength() == 2 && p[0].ValueKind == JsonValueKind.Number && p[1].ValueKind == JsonValueKind.Number
                ? (p[0].GetDouble(), p[1].GetDouble())
                : throw new DataFileException($"{where}: each point must be [x, y].")).ToList();
        if (points.Count < 3)
            throw new DataFileException($"{where} needs at least 3 points.");
        return points;
    }
}
