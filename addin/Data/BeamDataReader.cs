using System.Text.Json;

namespace Bimtech.RevitMcp.Data;

// Reads and validates a beam/1.0 file. Refuses another schema major version, a unit other than mm, missing columns,
// a non-positive size and zero-length beams, instead of guessing. b / h may be null (no size in the drawing).
public static class BeamDataReader
{
    public const string SchemaPrefix = "beam/1.";

    private static readonly string[] RequiredColumns = ["name", "type", "b", "h", "width", "x1", "y1", "x2", "y2", "grid_start", "grid_end"];

    public static BeamDataSet Parse(string json)
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

            var source = new BeamDataSource(
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
                Json.OptionalNumber(meta, "top_offset_mm"));
            return new BeamDataSet(source, ReadBeams(Json.RequireObject(root, "beams")),
                Json.StringList(root, "issues"), Json.StringList(root, "decisions"));
        }
    }

    private static List<DataBeam> ReadBeams(JsonElement table)
    {
        var (cols, rows) = Json.Table(table, "beams", RequiredColumns);
        int Col(string name) => cols.IndexOf(name);
        var list = new List<DataBeam>();
        var index = 0;
        foreach (var row in rows)
        {
            var where = $"beams.rows[{index++}]";
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != cols.Count)
                throw new DataFileException($"{where} does not have {cols.Count} values.");
            string? Text(string name) => Col(name) >= 0 && row[Col(name)].ValueKind == JsonValueKind.String ? row[Col(name)].GetString() : null;
            double? Maybe(string name) => Col(name) >= 0 && row[Col(name)].ValueKind == JsonValueKind.Number && double.IsFinite(row[Col(name)].GetDouble())
                ? row[Col(name)].GetDouble()
                : null;
            double Number(string name) => Maybe(name) ?? throw new DataFileException($"{where}.{name} is not a number.");
            var flags = Col("flags") >= 0 && row[Col("flags")].ValueKind == JsonValueKind.Array
                ? row[Col("flags")].EnumerateArray().Where(f => f.ValueKind == JsonValueKind.String).Select(f => f.GetString()!).ToList()
                : [];

            var (x1, y1, x2, y2) = (Number("x1"), Number("y1"), Number("x2"), Number("y2"));
            var (b, h) = (Maybe("b"), Maybe("h"));
            if (b is <= 0 || h is <= 0)
                throw new DataFileException($"{where}: b and h must be positive (or null when the drawing gives no size).");
            if (Math.Abs(x2 - x1) + Math.Abs(y2 - y1) < 1)
                throw new DataFileException($"{where}: the beam has no length.");
            list.Add(new DataBeam(Text("name"), Text("type") ?? "?", b, h, Number("width"), x1, y1, x2, y2,
                Text("support_start") ?? "free", Text("support_end") ?? "free", Text("grid_start") ?? "", Text("grid_end") ?? "",
                Text("size_from"), flags));
        }
        if (list.Count == 0)
            throw new DataFileException("beams.rows is empty.");
        return list;
    }
}
