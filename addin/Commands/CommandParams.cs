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

    public CommandParams(JsonElement value)
    {
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
            throw RpcException.InvalidParams(name, "expected an integer.");
        if (number < min || number > max)
            throw RpcException.InvalidParams(name, $"must be between {min} and {max}.");
        return number;
    }

    public bool GetBool(string name, bool defaultValue)
    {
        if (!TryGet(name, out var value)) return defaultValue;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw RpcException.InvalidParams(name, "expected true or false."),
        };
    }

    // Matches enum names case-insensitively, e.g. "FloorPlan" for ViewType.FloorPlan.
    public TEnum GetEnum<TEnum>(string name, TEnum defaultValue) where TEnum : struct, Enum
    {
        var text = GetString(name, 128);
        if (text is null) return defaultValue;
        if (!int.TryParse(text, out _) && Enum.TryParse<TEnum>(text, ignoreCase: true, out var parsed))
            return parsed;
        throw RpcException.InvalidParams(name,
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

    private static string ReadString(string name, JsonElement value, int maxLength)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw RpcException.InvalidParams(name, "expected a string.");
        var text = value.GetString()!;
        if (text.Length > maxLength)
            throw RpcException.InvalidParams(name, $"must be at most {maxLength} characters.");
        return text;
    }

    private static List<JsonElement> ReadArray(string name, JsonElement value, int maxCount)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw RpcException.InvalidParams(name, "expected an array.");
        if (value.GetArrayLength() > maxCount)
            throw RpcException.InvalidParams(name, $"must contain at most {maxCount} items.");
        return value.EnumerateArray().ToList();
    }

    private static ElementId ReadElementId(string name, JsonElement value)
    {
        long id = 0;
        var valid = value.ValueKind == JsonValueKind.String
            ? long.TryParse(value.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out id)
            : value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out id);
        if (!valid) throw RpcException.InvalidParams(name, "expected an element ID such as \"12345\".");
        return new ElementId(id);
    }

    private static RpcException Missing(string name) => RpcException.InvalidParams(name, "is required.");
}
