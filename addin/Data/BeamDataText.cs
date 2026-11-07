namespace Bimtech.RevitMcp.Data;

// Vietnamese text shown to the reviewer for beams from data. The only file of the beam data code with non-English text.
internal static class BeamDataText
{
    public const string Summary = "{0} · {1} dầm · {2:0.0} m · {3}";
    public const string SummaryLook = "{0} · {1} dầm cần xem · {2:0.0} m · {3}";
    public const string Title = "Dựng {0} dầm tại {1} từ dữ liệu {2}";

    public const string FromData = "Từ dữ liệu {0} ({1}, ảnh chụp {2}, hash {3}), vị trí theo lưới {4}.";
    public const string Supports = "Đầu dầm đã kéo tới tâm cột, tim vách hoặc tim dầm đỡ: Revit tự cắt và nối.";
    public const string Existing = "Dầm trùng trục với dầm đã có trong model sẽ được bỏ qua lúc ghi.";
    public const string Top = "Mặt trên dầm tại {0}, lệch {1:0} mm (theo dữ liệu).";
    public const string Names = "Tên dầm (ghi vào Mark): {0}";
    public const string SizeDerived = "Có {0} dầm lấy kích thước không ghi trực tiếp trên tên ({1}).";
    public const string NewType = "Sẽ tạo Type mới trong Family: kiểm tra trước khi ghi.";
    public const string Flagged = "Dầm bị đánh dấu khi trích xuất ({0}): xem trên mặt bằng trước khi dựng.";
    public const string At = "Vị trí: {0}";
    public const string TypeInFamily = "tạo Type {0} trong {1}";
    public const string NoType = "chưa có Type";
    public const string NoTypeProblem = "Chưa có Type dầm {0}: gán trong 'types' hoặc truyền templateFamilyId (load_default_family kind=beam).";

    public static readonly IReadOnlyDictionary<string, string> FlagNames = new Dictionary<string, string>
    {
        ["width_mismatch"] = "rộng đo lệch tên",
        ["multi_label"] = "gần nhiều tên",
        ["inner_edge"] = "có nét ở giữa",
    };

    public static readonly IReadOnlyDictionary<string, string> SizeFromNames = new Dictionary<string, string>
    {
        ["fragment"] = "dòng kia của tên",
        ["same_name"] = "chữ cùng tên",
        ["neighbour"] = "nhịp liền kề",
    };
}
