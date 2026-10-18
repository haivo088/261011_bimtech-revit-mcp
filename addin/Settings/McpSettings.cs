using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bimtech.RevitMcp.Settings;

// User settings shared by the add-in and the Node server, stored in
// %APPDATA%\BIMTECH\RevitMcp\settings.json. Immutable: change with "with" and SettingsStore.Save.
public sealed record McpSettings
{
    public const int DefaultPort = 8081;
    public const int DefaultMaxElementsPerWrite = 5000;
    public const int MaxElementsPerWriteLimit = 20000;

    public int Version { get; init; } = 1;

    // TCP port of the bridge. The server reads the same file, so both sides always agree.
    public int Port { get; init; } = DefaultPort;

    // Start the bridge when Revit starts, without pressing the ribbon button.
    public bool AutoStartBridge { get; init; }

    // Blocks every command that changes the model, whatever DisabledCommands says.
    public bool ReadOnlyMode { get; init; }

    // A plan whose every item is sure (nothing to review, nothing blocked) is approved on the person's behalf as soon as
    // it is proposed, so Claude commits it without the review window. The person turns it on; Claude never approves.
    public bool AutoCommitSure { get; init; }

    // Pressing "Duyệt kế hoạch" in the review window also writes the plan into the model at once (one Ctrl+Z), so the
    // person need not go back to Claude to say they are done. Read-only mode and a switched-off commit_plan still apply.
    public bool CommitOnApprove { get; init; } = true;

    // Upper bound for elements one write call may create, e.g. beams in propose_beams.
    public int MaxElementsPerWrite { get; init; } = DefaultMaxElementsPerWrite;

    // Methods switched off by the user. Stored as an exclusion list so new commands start enabled.
    public IReadOnlyList<string> DisabledCommands { get; init; } = [];

    public bool IsDisabled(string method) => DisabledCommands.Contains(method, StringComparer.Ordinal);

    // Out-of-range values from a hand-edited file fall back to safe values instead of failing.
    public McpSettings Normalized() => this with
    {
        Port = Port is >= 1024 and <= 65535 ? Port : DefaultPort,
        MaxElementsPerWrite = MaxElementsPerWrite is >= 1 and <= MaxElementsPerWriteLimit
            ? MaxElementsPerWrite : DefaultMaxElementsPerWrite,
        DisabledCommands = (DisabledCommands ?? []).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().Order().ToList(),
    };
}

// Holds the current settings. Read from the TCP thread and Revit's thread, replaced from the settings window;
// a single reference swap keeps every reader consistent without locks.
public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static McpSettings _current = new();

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BIMTECH", "RevitMcp", "settings.json");

    public static McpSettings Current => Volatile.Read(ref _current);

    // Why the last load fell back to defaults, shown in the settings window; null when the file was fine or absent.
    public static string? LoadProblem { get; private set; }

    public static event Action<McpSettings>? Changed;

    public static McpSettings Load()
    {
        LoadProblem = null;
        McpSettings settings;
        try
        {
            settings = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<McpSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new McpSettings()
                : new McpSettings();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            LoadProblem = $"Không đọc được {FilePath}: {exception.Message}. Đang dùng cài đặt mặc định.";
            settings = new McpSettings();
        }
        Volatile.Write(ref _current, settings.Normalized());
        return Current;
    }

    // Writes to a temporary file first so a crash never leaves a half-written settings.json.
    public static void Save(McpSettings settings)
    {
        var normalized = settings.Normalized();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(normalized, JsonOptions));
        File.Move(temporary, FilePath, overwrite: true);
        LoadProblem = null;
        Volatile.Write(ref _current, normalized);
        Changed?.Invoke(normalized);
    }
}
