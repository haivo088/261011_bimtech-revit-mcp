using System.Text.Json;

namespace Bimtech.RevitMcp.Data;

// Reads and validates a column/1.0 file. Refuses another schema major version, a unit other than mm, missing
// columns, rotations other than 0 / 90 and non-positive sizes, instead of guessing.
public static class ColumnDataReader
{
    public const string SchemaPrefix = "column/1.";

    private static readonly string[] RequiredColumns = ["type", "kind", "x", "y", "b", "h", "rotation_deg", "grid_ref"];

    public static ColumnDataSet Parse(string json)
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
            var level = meta.TryGetProperty("level", out var lv) && lv.ValueKind == JsonValueKind.Object ? lv : (JsonElement?)null;

            var source = new ColumnDataSource(
                schema,
                Json.OptionalString(meta, "tool"),
                Json.OptionalString(meta, "src"),
                Json.OptionalString(meta, "src_hash"),
                Json.OptionalString(meta, "fix_file"),
                Json.OptionalString(meta, "origin"),
                Json.OptionalString(meta, "grid_file"),
                Json.OptionalString(meta, "grid_hash"),
                level is { } l ? Json.OptionalString(l, "base") : null,
                level is { } t ? Json.OptionalString(t, "top") : null);

            var checks = root.TryGetProperty("checks", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;
            var wallLike = root.TryGetProperty("wall_like", out var w) && w.ValueKind == JsonValueKind.Object
                && w.TryGetProperty("rows", out var wr) && wr.ValueKind == JsonValueKind.Array ? wr.GetArrayLength() : 0;
            return new ColumnDataSet(
                source,
                ReadColumns(Json.RequireObject(root, "columns")),
                new ColumnDataChecks(
                    Json.OptionalBool(checks, "rectangles") ?? false,
                    Json.OptionalBool(checks, "no_duplicates") ?? false,
                    (int)(Json.OptionalNumber(checks, "dyn_size_ok") ?? 0),
                    (int)(Json.OptionalNumber(checks, "overlaps") ?? 0)),
                Json.StringList(root, "empty_crossings"),
                wallLike,
                Json.StringList(root, "issues"),
                Json.StringList(root, "decisions"));
        }
    }

    private static List<DataColumn> ReadColumns(JsonElement table)
    {
        var (cols, rows) = Json.Table(table, "columns", RequiredColumns);
        int Col(string name) => cols.IndexOf(name);
        var list = new List<DataColumn>();
        var index = 0;
        foreach (var row in rows)
        {
            var where = $"columns.rows[{index++}]";
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != cols.Count)
                throw new DataFileException($"{where} does not have {cols.Count} values.");
            string? Text(string name) => Col(name) >= 0 && row[Col(name)].ValueKind == JsonValueKind.String ? row[Col(name)].GetString() : null;
            double Number(string name) => row[Col(name)].ValueKind == JsonValueKind.Number && double.IsFinite(row[Col(name)].GetDouble())
                ? row[Col(name)].GetDouble()
                : throw new DataFileException($"{where}.{name} is not a number.");

            var (b, h, rotation) = (Number("b"), Number("h"), Number("rotation_deg"));
            if (b <= 0 || h <= 0)
                throw new DataFileException($"{where}: b and h must be positive.");
            if (rotation is not (0 or 90))
                throw new DataFileException($"{where}.rotation_deg is {rotation}; only 0 and 90 are supported.");
            list.Add(new DataColumn(
                Text("type") ?? throw new DataFileException($"{where}.type is empty."),
                Text("kind"), Number("x"), Number("y"), b, h, rotation, Text("grid_ref") ?? "", Text("handle"), Text("insert")));
        }
        if (list.Count == 0)
            throw new DataFileException("columns.rows is empty.");
        return list;
    }
}
