using System.Text.Json;

namespace Bimtech.RevitMcp.Data;

// Reads and validates a grid/1.0 file. Rejects what this version cannot place correctly instead of guessing:
// another schema major version, a unit other than mm, a rotated grid system, missing columns.
public static class GridDataReader
{
    public const string SchemaPrefix = "grid/1.";
    public const string UnnamedMark = "?";
    // Larger rotations need a rotated layout, which this version does not do yet.
    public const double MaxRotationDeg = 0.01;

    private static readonly string[] RequiredColumns = ["name", "axis", "pos", "start", "end"];

    public static GridDataSet Parse(string json)
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
            var rotation = Json.OptionalNumber(meta, "rotation_deg") ?? 0;
            if (Math.Abs(rotation) > MaxRotationDeg)
                throw new DataFileException($"meta.rotation_deg is {rotation}; rotated grid systems are not supported yet.");
            var originCad = meta.TryGetProperty("origin_cad", out var oc) && oc.ValueKind == JsonValueKind.Array && oc.GetArrayLength() == 2
                ? (oc[0].GetDouble(), oc[1].GetDouble())
                : (0.0, 0.0);

            var source = new GridDataSource(
                schema,
                Json.OptionalString(meta, "tool"),
                Json.OptionalString(meta, "created"),
                Json.OptionalString(meta, "src"),
                Json.OptionalString(meta, "src_hash"),
                Json.OptionalString(meta, "fix_file"),
                Json.OptionalString(meta, "origin"),
                originCad.Item1,
                originCad.Item2);

            var grids = ReadGrids(Json.RequireObject(root, "grids"));
            var stats = root.TryGetProperty("stats", out var s) && s.ValueKind == JsonValueKind.Object ? s : (JsonElement?)null;
            bool? selfCheck = stats is { } st && st.TryGetProperty("self_check", out var sc) && sc.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? sc.GetBoolean()
                : null;
            return new GridDataSet(source, grids, Json.StringList(root, "issues"), Json.StringList(root, "decisions"), selfCheck);
        }
    }

    private static List<DataGrid> ReadGrids(JsonElement table)
    {
        if (!table.TryGetProperty("cols", out var colsElement) || colsElement.ValueKind != JsonValueKind.Array)
            throw new DataFileException("grids.cols is missing.");
        var cols = colsElement.EnumerateArray().Select(c => c.GetString() ?? "").ToList();
        foreach (var required in RequiredColumns)
            if (!cols.Contains(required))
                throw new DataFileException($"grids.cols has no '{required}' column.");
        int Col(string name) => cols.IndexOf(name);

        if (!table.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new DataFileException("grids.rows is missing.");
        var grids = new List<DataGrid>();
        var index = 0;
        foreach (var row in rows.EnumerateArray())
        {
            var where = $"grids.rows[{index++}]";
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != cols.Count)
                throw new DataFileException($"{where} does not have {cols.Count} values.");
            string? Text(string name) => Col(name) < 0 ? null : row[Col(name)].ValueKind == JsonValueKind.String ? row[Col(name)].GetString() : null;
            double Number(string name) => row[Col(name)].ValueKind == JsonValueKind.Number && double.IsFinite(row[Col(name)].GetDouble())
                ? row[Col(name)].GetDouble()
                : throw new DataFileException($"{where}.{name} is not a number.");

            var axis = Text("axis");
            if (axis is not ("X" or "Y"))
                throw new DataFileException($"{where}.axis is '{axis}'; expected 'X' or 'Y'.");
            var name = Text("name");
            if (string.IsNullOrWhiteSpace(name))
                throw new DataFileException($"{where}.name is empty.");
            grids.Add(new DataGrid(
                name.Trim(), axis, Text("kind") ?? "main", Number("pos"), Number("start"), Number("end"),
                Text("bubble") ?? "none", Text("handle"), Text("bubble_handle")));
        }
        if (grids.Count == 0)
            throw new DataFileException("grids.rows is empty.");
        return grids;
    }
}
