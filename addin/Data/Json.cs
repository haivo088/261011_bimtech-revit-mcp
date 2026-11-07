using System.Text.Json;

namespace Bimtech.RevitMcp.Data;

// Small JSON helpers shared by the data readers. Missing required values throw DataFileException naming the field.
internal static class Json
{
    public static JsonElement RequireObject(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw new DataFileException($"'{name}' is missing.");

    public static string RequireString(JsonElement parent, string name, string where) =>
        OptionalString(parent, name) ?? throw new DataFileException($"{where}.{name} is missing.");

    public static string? OptionalString(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static double? OptionalNumber(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    public static bool? OptionalBool(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    public static List<string> StringList(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList()
            : [];

    // A {"cols": [...], "rows": [[...], ...]} table: its column names (all `required` present) and its rows.
    public static (List<string> Cols, List<JsonElement> Rows) Table(JsonElement table, string where, IEnumerable<string> required)
    {
        if (!table.TryGetProperty("cols", out var colsElement) || colsElement.ValueKind != JsonValueKind.Array)
            throw new DataFileException($"{where}.cols is missing.");
        var cols = colsElement.EnumerateArray().Select(c => c.GetString() ?? "").ToList();
        foreach (var name in required)
            if (!cols.Contains(name))
                throw new DataFileException($"{where}.cols has no '{name}' column.");
        if (!table.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new DataFileException($"{where}.rows is missing.");
        return (cols, rows.EnumerateArray().ToList());
    }
}
