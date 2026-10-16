using System.IO;
using System.Text.Json;

namespace Bimtech.RevitMcp.DevTools;

// Every send_code run, one JSON line each in logs/code-runs.jsonl at the repository root (gitignored), so Claude can
// see the same kind of job coming back and propose packaging it as a command set. Losing a line never fails a run.
public static class CodeHistory
{
    public const int MaxCodeChars = 4000;

    public sealed record Entry(
        DateTimeOffset Time, string Kind, string? Purpose, string? Script, bool Write, bool Ok, string Stage,
        string CodeHash, string? Code, string? Document);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly object Lock = new();

    public static void Append(string? path, Entry entry)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var trimmed = entry with { Code = entry.Code is { Length: > MaxCodeChars } code ? code[..MaxCodeChars] + "…" : entry.Code };
            lock (Lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, JsonSerializer.Serialize(trimmed, Json) + "\n");
            }
        }
        catch (Exception)
        {
            // History is a convenience; the run itself already happened.
        }
    }

    // The last `count` entries, newest first; unreadable lines are skipped.
    public static List<Entry> Read(string? path, int count)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return [];
        string[] lines;
        lock (Lock) lines = File.ReadAllLines(path);
        var entries = new List<Entry>();
        for (var i = lines.Length - 1; i >= 0 && entries.Count < count; i--)
        {
            try
            {
                if (JsonSerializer.Deserialize<Entry>(lines[i], Json) is { } entry) entries.Add(entry);
            }
            catch (JsonException)
            {
            }
        }
        return entries;
    }
}
