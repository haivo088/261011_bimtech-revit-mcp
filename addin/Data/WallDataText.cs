namespace Bimtech.RevitMcp.Data;

// Vietnamese text shown to the reviewer for walls from data. The only file of the wall data code with non-English text.
internal static class WallDataText
{
    public const string Summary = "{0} · {1} đoạn · {2:0.0} m · {3}";
    public const string SummaryLook = "{0} · {1} đoạn cần xem · {2:0.0} m · {3}";
    public const string Title = "Dựng {0} đoạn vách {1} → {2} từ dữ liệu {3}";

    public const string FromData = "Từ dữ liệu {0} ({1}, ảnh chụp {2}, hash {3}), vị trí theo lưới {4}.";
    public const string Checked = "Đã kiểm tra ngoài Revit: mọi đường bao tách đúng thành đoạn vách (tổng diện tích bằng nhau).";
    public const string NotChecked = "Đường bao của đoạn này tách chưa khớp diện tích khi trích xuất: xem lại hình dạng.";
    public const string NewType = "Sẽ tạo Type mới: kiểm tra cấu tạo lớp và bề dày trước khi ghi.";
    public const string Flagged = "Đoạn bị đánh dấu khi trích xuất ({0}): xem trên mặt bằng trước khi dựng.";
    public const string Joins = "Đầu vách đã kéo tới tim vách giao (góc L, chữ T): Revit tự nối.";
    public const string Existing = "Phần tim vách đã có vách trong model sẽ được bỏ qua lúc ghi.";
    public const string At = "Vị trí: {0}";
    public const string Openings = "{0} cửa (khe trên bản vẽ đã lấp thành vách liền, khoét lỗ cửa khi ghi): {1}";
    public const string NoHead = "Có cửa chưa có chiều cao (thiếu wall_openings.head_mm trong _fix.json): vách liền nhưng không khoét.";
    public const string TypeExact = "Type {0}";
    public const string TypeNew = "Type mới {0} (mẫu {1})";
    public const string NoTemplate = "chưa có Type";
    public const string NoTemplateProblem = "Model chưa có Basic Wall Type có cấu tạo lớp để làm mẫu cho vách {0}: truyền templateTypeId.";

    public static readonly IReadOnlyDictionary<string, string> FlagNames = new Dictionary<string, string>
    {
        ["sliver"] = "mảnh nhỏ",
        ["odd_thickness"] = "dày lạ",
        ["overlaps_column"] = "chồng cột",
    };
}
