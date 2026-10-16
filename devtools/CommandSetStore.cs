using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Bimtech.RevitMcp.DevTools;

// Command sets: snippets for send_code that proved useful, with a name and typed parameters, kept in the repository
// (commandsets/ at its root) so they have history and travel with the code:
//   commandsets/<set>/set.json         { "description": "..." }
//   commandsets/<set>/<script>.cs      the snippet, same form as send_code's code; parameters come from ctx.Str("x")...
//   commandsets/<set>/<script>.json    { "description", "write", "params": [{ "name", "type", "description",
//                                        "default", "required" }], "notes": [] }
// Plain files and JSON only, no Revit, so it is tested outside Revit.
public sealed partial class CommandSetStore(string root)
{
    public static readonly string[] Types = ["string", "number", "boolean", "elementId", "string[]", "elementId[]"];

    public sealed record Param(string Name, string Type, string? Description = null, JsonElement? Default = null, bool Required = false);

    public sealed record Script(string Set, string Name, string Description, bool Write, IReadOnlyList<Param> Params, IReadOnlyList<string> Notes)
    {
        public string Id => $"{Set}/{Name}";
    }

    public sealed record Set(string Name, string Description, IReadOnlyList<Script> Scripts);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public string Root { get; } = root;

    public List<Set> List()
    {
        if (!Directory.Exists(Root)) return [];
        return Directory.GetDirectories(Root)
            .Select(folder => Path.GetFileName(folder))
            .Where(name => ValidName().IsMatch(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => new Set(name, ReadSetDescription(name),
                Directory.GetFiles(Path.Combine(Root, name), "*.cs")
                    .Select(file => Path.GetFileNameWithoutExtension(file))
                    .Where(script => ValidName().IsMatch(script))
                    .OrderBy(script => script, StringComparer.Ordinal)
                    .Select(script => ReadInfo(name, script))
                    .ToList()))
            .ToList();
    }

    // id: "set/script".
    public (Script Info, string Code) Load(string id)
    {
        var (set, name) = SplitId(id);
        var code = Path.Combine(Root, set, name + ".cs");
        if (!File.Exists(code))
            throw new CommandSetException($"No script \"{id}\" in {Root}. Use command_set with action \"list\".");
        return (ReadInfo(set, name), File.ReadAllText(code));
    }

    public Script Save(Script script, string code, string? setDescription, bool overwrite)
    {
        if (!ValidName().IsMatch(script.Set) || !ValidName().IsMatch(script.Name))
            throw new CommandSetException("Set and script names use lowercase letters, digits and '-', e.g. \"family-tcvn/rename-params\".");
        if (string.IsNullOrWhiteSpace(script.Description))
            throw new CommandSetException("A script needs a description.");
        ValidateParams(script.Params);
        var folder = Path.Combine(Root, script.Set);
        var codePath = Path.Combine(folder, script.Name + ".cs");
        if (File.Exists(codePath) && !overwrite)
            throw new CommandSetException($"Script \"{script.Id}\" already exists. Pass overwrite=true to replace it.");

        Directory.CreateDirectory(folder);
        var setPath = Path.Combine(folder, "set.json");
        if (setDescription is not null || !File.Exists(setPath))
            File.WriteAllText(setPath, JsonSerializer.Serialize(new { description = setDescription ?? script.Set }, Json) + "\n");
        File.WriteAllText(codePath, code.TrimEnd() + "\n");
        File.WriteAllText(Path.Combine(folder, script.Name + ".json"), JsonSerializer.Serialize(new
        {
            description = script.Description,
            write = script.Write,
            @params = script.Params.Select(p => new { name = p.Name, type = p.Type, description = p.Description, @default = p.Default, required = p.Required ? true : (bool?)null }),
            notes = script.Notes,
        }, Json) + "\n");
        return ReadInfo(script.Set, script.Name);
    }

    // The arguments a script runs with: each given value checked against its parameter's type, defaults filled in,
    // required ones present, unknown names refused. Returns a JSON object for ctx.Str/Num/....
    public static JsonElement BindArgs(Script script, JsonElement? args)
    {
        var given = args is { ValueKind: JsonValueKind.Object } value
            ? value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value)
            : [];
        var unknown = given.Keys.Where(name => script.Params.All(p => p.Name != name)).ToList();
        if (unknown.Count > 0)
            throw new CommandSetException($"Unknown argument(s) for {script.Id}: {string.Join(", ", unknown)}. "
                + $"Parameters: {string.Join(", ", script.Params.Select(p => $"{p.Name} ({p.Type})"))}.");
        var bound = new JsonObject();
        foreach (var param in script.Params)
        {
            if (given.TryGetValue(param.Name, out var arg))
            {
                if (!Fits(param.Type, arg))
                    throw new CommandSetException($"Argument \"{param.Name}\" of {script.Id} must be {param.Type}.");
                bound[param.Name] = JsonNode.Parse(arg.GetRawText());
            }
            else if (param.Default is { } fallback)
                bound[param.Name] = JsonNode.Parse(fallback.GetRawText());
            else if (param.Required)
                throw new CommandSetException($"Argument \"{param.Name}\" ({param.Type}) is required by {script.Id}.");
        }
        return JsonSerializer.SerializeToElement(bound);
    }

    public static void ValidateParams(IReadOnlyList<Param> parameters)
    {
        foreach (var param in parameters)
        {
            if (!ParamName().IsMatch(param.Name))
                throw new CommandSetException($"Parameter name \"{param.Name}\" must be letters and digits, starting with a letter.");
            if (!Types.Contains(param.Type))
                throw new CommandSetException($"Parameter \"{param.Name}\": type must be one of {string.Join(", ", Types)}.");
            if (param.Default is { } fallback && !Fits(param.Type, fallback))
                throw new CommandSetException($"Parameter \"{param.Name}\": default must be {param.Type}.");
        }
        var doubled = parameters.GroupBy(p => p.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (doubled.Count > 0) throw new CommandSetException($"Parameter(s) listed twice: {string.Join(", ", doubled)}.");
    }

    private static bool Fits(string type, JsonElement value) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "elementId" => IsId(value),
        "string[]" => value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String),
        "elementId[]" => value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(IsId),
        _ => false,
    };

    private static bool IsId(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number ? value.TryGetInt64(out _)
        : value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out _);

    private string ReadSetDescription(string set)
    {
        var path = Path.Combine(Root, set, "set.json");
        try
        {
            return File.Exists(path) && JsonNode.Parse(File.ReadAllText(path))?["description"]?.GetValue<string>() is { } text ? text : "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    private Script ReadInfo(string set, string name)
    {
        var path = Path.Combine(Root, set, name + ".json");
        if (!File.Exists(path)) return new Script(set, name, "", false, [], []);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var parameters = root.TryGetProperty("params", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(p => new Param(
                    Text(p, "name") ?? "", Text(p, "type") ?? "string", Text(p, "description"),
                    p.TryGetProperty("default", out var fallback) ? fallback.Clone() : null,
                    p.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.True)).ToList()
                : [];
            var notes = root.TryGetProperty("notes", out var noteList) && noteList.ValueKind == JsonValueKind.Array
                ? noteList.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.String).Select(n => n.GetString()!).ToList()
                : [];
            return new Script(set, name, Text(root, "description") ?? "",
                root.TryGetProperty("write", out var write) && write.ValueKind == JsonValueKind.True, parameters, notes);
        }
        catch (JsonException exception)
        {
            throw new CommandSetException($"{path} is not valid JSON: {exception.Message}");
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static (string Set, string Name) SplitId(string id)
    {
        var parts = id.Split('/');
        if (parts.Length != 2 || !ValidName().IsMatch(parts[0]) || !ValidName().IsMatch(parts[1]))
            throw new CommandSetException($"Script names look like \"set/script\" (lowercase, digits, '-'), not \"{id}\".");
        return (parts[0], parts[1]);
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,49}$")]
    private static partial Regex ValidName();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]{0,39}$")]
    private static partial Regex ParamName();
}

public sealed class CommandSetException(string message) : Exception(message);
