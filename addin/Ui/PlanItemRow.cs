using System.ComponentModel;
using System.Text.Json;
using Bimtech.RevitMcp.Planning;

namespace Bimtech.RevitMcp.Ui;

// One line in the review window. Only Status changes while the window is open.
public sealed class PlanItemRow : INotifyPropertyChanged
{
    private PlanItemStatus _status;

    public PlanItemRow(PlanItem item)
    {
        ItemId = item.ItemId;
        Element = DescribeElement(item);
        Change = DescribeChange(item.Params);
        Reasons = string.Join(" ", item.Reasons);
        Detail = ReadString(item.Params, "detail") ?? "";
        ElementIds = ReadStrings(item.Params, "elementIds") is { Count: > 0 } ids ? ids
            : (ReadString(item.Params, "elementId") ?? ReadString(item.Source, "elementId")) is { } id ? [id] : [];
        IsAuto = item.Tier == PlanTier.Auto;
        AutoStatus = IsAuto ? item.AutoStatus ?? PlanItemStatus.Approved : null;
        ConfidenceText = item.Confidence is { } confidence ? $"{Math.Round(confidence * 100):0}%" : "";
        (Verb, Noun) = Describe(item.Action);
        ElementCount = ReadInt(item.Source, "count") ?? 1;
        var shapes = PreviewShapes.From(item.Action, item.Params);
        Preview = shapes.Count == 0 ? PreviewGroup.None : new PreviewGroup(PreviewShapes.LevelId(item.Params), shapes);
        _status = item.Status;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ItemId { get; }
    public string Element { get; }
    public string Change { get; }
    public string Reasons { get; }
    // Long text shown below the list when the row is selected, e.g. the code of a send_code item. Empty for most items.
    public string Detail { get; }
    // Revit elements to select and zoom to when the row is selected: params.elementIds (e.g. a change_type group), else
    // the one elementId.
    public IReadOnlyList<string> ElementIds { get; }
    // Decided automatically: approved because its confidence reached PlanConfidence.AutoApproveFrom, or rejected as the
    // person did before (PlanTier.Auto).
    public bool IsAuto { get; }
    // What was decided for the person (Approved or Rejected); null for items they decide.
    public PlanItemStatus? AutoStatus { get; }
    // Still as decided for the person.
    public bool IsDecidedForPerson => AutoStatus is { } decided && Status == decided;
    // e.g. "95%"; empty when the command gives no confidence.
    public string ConfidenceText { get; }
    // Where the item's new elements will be, drawn in Revit while the row is selected. Empty for items on existing elements.
    public PreviewGroup Preview { get; }

    // What the item does to the model, e.g. "Tạo" "cột", and how many elements: "Tạo cột" in the list,
    // "Tạo 50 cột" in the result line.
    public string Verb { get; }
    public string Noun { get; }
    public int ElementCount { get; }
    public string ActionText => $"{Verb} {Noun}";

    public PlanItemStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            PropertyChanged?.Invoke(this, new(nameof(Status)));
            PropertyChanged?.Invoke(this, new(nameof(StatusText)));
        }
    }

    public string StatusText => Status switch
    {
        PlanItemStatus.Pending => "Cần xem",
        PlanItemStatus.Approved => IsDecidedForPerson ? "Tự duyệt" : "Đã duyệt",
        PlanItemStatus.Rejected => IsDecidedForPerson ? "Tự bỏ" : "Bỏ qua",
        PlanItemStatus.Applied => "Đã ghi",
        _ => "Lỗi",
    };

    // e.g. "Walls · Basic Wall (12345)".
    private static string DescribeElement(PlanItem item)
    {
        var category = ReadString(item.Source, "category");
        var name = ReadString(item.Source, "name");
        var id = ReadString(item.Source, "elementId") ?? ReadString(item.Params, "elementId");
        var label = string.Join(" · ", new[] { category, name }.Where(text => !string.IsNullOrEmpty(text)));
        return id is null ? label : $"{label} ({id})".TrimStart();
    }

    // Items with a "summary" param show it (e.g. "Trục B (ngang): y = 6000 mm, dài 25 m");
    // set_parameter items read "Comments: (trống) → Kiểm tra"; other actions show their params.
    private static string DescribeChange(JsonElement parameters)
    {
        if (ReadString(parameters, "summary") is { } summary) return summary;
        if (ReadString(parameters, "value") is { } value)
        {
            var current = ReadString(parameters, "currentValue");
            var change = $"{(string.IsNullOrEmpty(current) ? "(trống)" : current)} → {value}";
            return ReadString(parameters, "parameter") is { } name ? $"{name}: {change}" : change;
        }
        var raw = parameters.ValueKind == JsonValueKind.Undefined ? "" : parameters.GetRawText();
        return raw.Length <= 120 ? raw : raw[..117] + "...";
    }

    private static (string Verb, string Noun) Describe(string action) => action switch
    {
        "create_columns" => ("Tạo", "cột"),
        "create_walls" => ("Tạo", "vách"),
        "create_beams" => ("Tạo", "dầm"),
        "create_pile_cap" => ("Tạo", "đài cọc"),
        "create_floor" => ("Tạo", "sàn"),
        "create_grid" => ("Tạo", "trục"),
        "create_level" => ("Tạo", "cao độ"),
        "delete_level" => ("Xoá", "cao độ"),
        "move_cad" => ("Dời", "CAD"),
        "set_parameter" => ("Đặt", "tham số"),
        "run_code" => ("Chạy", "code"),
        "change_type" => ("Đổi Type", "phần tử"),
        _ => ("Sửa", "mục"),
    };

    // A JSON array of IDs, as strings or numbers.
    private static List<string>? ReadStrings(JsonElement? json, string name) =>
        json is { ValueKind: JsonValueKind.Object } value
            && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Array
            ? property.EnumerateArray()
                .Select(entry => entry.ValueKind switch
                {
                    JsonValueKind.String => entry.GetString(),
                    JsonValueKind.Number => entry.GetRawText(),
                    _ => null,
                })
                .OfType<string>().ToList()
            : null;

    private static int? ReadInt(JsonElement? json, string name) =>
        json is { ValueKind: JsonValueKind.Object } value
            && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number)
            ? number
            : null;

    private static string? ReadString(JsonElement? json, string name) =>
        json is { ValueKind: JsonValueKind.Object } value
            && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
