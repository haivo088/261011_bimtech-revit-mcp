using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Protocol;

namespace Bimtech.RevitMcp.Commands;

// Typed, validated access to the JSON "params" object of a request.
// Every failure becomes an InvalidParams error that names the parameter,
// so Claude can correct the call instead of seeing a stack trace.
// The MCP server validates input too; this side protects Revit from any caller.
public sealed class CommandParams
{
    private readonly JsonElement _root;
    private readonly string _prefix;

    public CommandParams(JsonElement value) : this(value, "") { }

    // prefix names a nested object in error messages, e.g. "region." gives "region.minX".
    private CommandParams(JsonElement value, string prefix)
    {
        _prefix = prefix;
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return;
        if (value.ValueKind != JsonValueKind.Object)
            throw new RpcException(RpcErrorCodes.InvalidParams, "params must be a JSON object.");
        _root = value;
    }

    public bool Has(string name) => TryGet(name, out _);

    public string? GetString(string name, int maxLength = 4096) =>
        TryGet(name, out var value) ? ReadString(name, value, maxLength) : null;

    public string RequireString(string name, int maxLength = 4096) =>
        GetString(name, maxLength) ?? throw Missing(name);

    public int GetInt(string name, int defaultValue, int min = int.MinValue, int max = int.MaxValue)
    {
        if (!TryGet(name, out var value)) return defaultValue;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
            throw Invalid(name, "expected an integer.");
        if (number < min || number > max)
            throw Invalid(name, $"must be between {min} and {max}.");
        return number;
    }

    public double GetDouble(string name, double defaultValue, double min = double.MinValue, double max = double.MaxValue)
    {
        if (!TryGet(name, out var value)) return defaultValue;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw Invalid(name, "expected a number.");
        if (number < min || number > max)
            throw Invalid(name, $"must be between {min} and {max}.");
        return number;
    }

    public double RequireDouble(string name, double min = double.MinValue, double max = double.MaxValue) =>
        Has(name) ? GetDouble(name, 0, min, max) : throw Missing(name);

    // A nested object, read with the same validation. Parameter names in errors stay prefixed, e.g. "region.minX".
    public CommandParams? GetObject(string name)
    {
        if (!TryGet(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Object)
            throw Invalid(name, "expected an object.");
        return new CommandParams(value, name + ".");
    }

    // An array of objects, each read with the same validation. Errors name the item, e.g. "levels[2].name".
    public IReadOnlyList<CommandParams> GetObjectList(string name, int maxCount = 1000)
    {
        if (!TryGet(name, out var value)) return [];
        return ReadArray(name, value, maxCount)
            .Select((item, index) => item.ValueKind == JsonValueKind.Object
                ? new CommandParams(item, $"{_prefix}{name}[{index}].")
                : throw Invalid($"{name}[{index}]", "expected an object."))
            .ToList();
    }

    // An object whose values are all strings, e.g. {"500x300": "12345"}.
    public IReadOnlyDictionary<string, string> GetStringMap(string name, int maxCount = 1000, int maxLength = 4096)
    {
        if (!TryGet(name, out var value)) return new Dictionary<string, string>();
        if (value.ValueKind != JsonValueKind.Object)
            throw Invalid(name, "expected an object whose values are strings.");
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (map.Count >= maxCount)
                throw Invalid(name, $"must contain at most {maxCount} entries.");
            map[property.Name] = ReadString($"{name}.{property.Name}", property.Value, maxLength);
        }
        return map;
    }

    // An object whose values are all numbers in [min, max], e.g. {"Đ1": 1200}.
    public IReadOnlyDictionary<string, double> GetNumberMap(string name, double min, double max, int maxCount = 1000)
    {
        if (!TryGet(name, out var value)) return new Dictionary<string, double>();
        if (value.ValueKind != JsonValueKind.Object)
            throw Invalid(name, "expected an object whose values are numbers.");
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (map.Count >= maxCount)
                throw Invalid(name, $"must contain at most {maxCount} entries.");
            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetDouble(out var number)
                || !double.IsFinite(number) || number < min || number > max)
                throw Invalid($"{name}.{property.Name}", $"expected a number between {min} and {max}.");
            map[property.Name] = number;
        }
        return map;
    }

    public bool GetBool(string name, bool defaultValue)
    {
        if (!TryGet(name, out var value)) return defaultValue;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Invalid(name, "expected true or false."),
        };
    }

    // Matches enum names case-insensitively, e.g. "FloorPlan" for ViewType.FloorPlan.
    public TEnum GetEnum<TEnum>(string name, TEnum defaultValue) where TEnum : struct, Enum
    {
        var text = GetString(name, 128);
        if (text is null) return defaultValue;
        if (!int.TryParse(text, out _) && Enum.TryParse<TEnum>(text, ignoreCase: true, out var parsed))
            return parsed;
        throw Invalid(name,
            $"unknown value '{text}'. Allowed: {string.Join(", ", Enum.GetNames<TEnum>())}.");
    }

    public IReadOnlyList<string> GetStringList(string name, int maxCount = 1000, int maxLength = 4096)
    {
        if (!TryGet(name, out var value)) return [];
        var array = ReadArray(name, value, maxCount);
        return array.Select((item, index) => ReadString($"{name}[{index}]", item, maxLength)).ToList();
    }

    // Element IDs travel as strings so 64-bit values survive JavaScript; numbers are accepted too.
    public ElementId RequireElementId(string name) =>
        TryGet(name, out var value) ? ReadElementId(name, value) : throw Missing(name);

    public IReadOnlyList<ElementId> GetElementIds(string name, int maxCount = 1000)
    {
        if (!TryGet(name, out var value)) return [];
        var array = ReadArray(name, value, maxCount);
        return array.Select((item, index) => ReadElementId($"{name}[{index}]", item)).ToList();
    }

    // A null value counts as missing, so optional params may be sent as null.
    private bool TryGet(string name, out JsonElement value)
    {
        value = default;
        return _root.ValueKind == JsonValueKind.Object
            && _root.TryGetProperty(name, out value)
            && value.ValueKind != JsonValueKind.Null;
    }

    private string ReadString(string name, JsonElement value, int maxLength)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw Invalid(name, "expected a string.");
        var text = value.GetString()!;
        if (text.Length > maxLength)
            throw Invalid(name, $"must be at most {maxLength} characters.");
        return text;
    }

    private List<JsonElement> ReadArray(string name, JsonElement value, int maxCount)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw Invalid(name, "expected an array.");
        if (value.GetArrayLength() > maxCount)
            throw Invalid(name, $"must contain at most {maxCount} items.");
        return value.EnumerateArray().ToList();
    }

    private ElementId ReadElementId(string name, JsonElement value)
    {
        long id = 0;
        var valid = value.ValueKind == JsonValueKind.String
            ? long.TryParse(value.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out id)
            : value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out id);
        if (!valid) throw Invalid(name, "expected an element ID such as \"12345\".");
        return new ElementId(id);
    }

    private RpcException Missing(string name) => Invalid(name, "is required.");

    private RpcException Invalid(string name, string message) => RpcException.InvalidParams(_prefix + name, message);
}
