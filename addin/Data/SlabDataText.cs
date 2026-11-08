namespace Bimtech.RevitMcp.Data;

// Vietnamese text shown to the reviewer for slabs from data. The only file of the slab data code with non-English text.
internal static class SlabDataText
{
    public const string Summary = "{0} ({1}) · dày {2:0} · {3:0.0} m² · {4} vòng trong · {5}";
    public const string Title = "Dựng {0} sàn tại {1} từ dữ liệu {2}";

    public const string FromData = "Từ dữ liệu {0} ({1}, ảnh chụp {2}, hash {3}), vị trí theo lưới {4}.";
    public const string Outline = "Đường bao = mọi thứ mép sàn bao lấy, kể cả vách và cột (sàn gối lên); Revit có thể báo sàn chồng vách.";
    public const string Top = "Mặt sàn tại {0}, lệch {1:0} mm (theo dữ liệu).";
    public const string Holes = "{0} lỗ khoét xuyên sàn (ô có dấu X): {1}";
    public const string NotCut = "{0} lỗ chạm mép sàn không khoét được (là phần khuyết của mép): {1}";
    public const string MainZone = "Sàn chính (phần không hatch).";
    public const string Zone = "Sàn vùng hatch {0}: bản vẽ không ghi công năng, đổi Type sau khi dựng nếu cần (Comments ghi tên vùng).";
    public const string Comment = "Vùng {0}";
    public const string InnerLoops = "{0} vòng trong khoét khỏi sàn này: lỗ mở và các sàn vùng nằm trong.";
    public const string Existing = "Sàn chồng lên sàn đã có trong model sẽ được bỏ qua lúc ghi.";
    public const string NewType = "Sẽ tạo Type mới: kiểm tra cấu tạo lớp trước khi ghi.";
    public const string TypeExact = "Type {0}";
    public const string TypeNew = "tạo Type {0}";
    public const string NoType = "chưa có Type";
    public const string NoTypeProblem = "Model chưa có Floor Type có cấu tạo lớp để tạo {0}: truyền floorTypeId.";

    public static readonly IReadOnlyDictionary<string, string> KindNames = new Dictionary<string, string>
    {
        ["lift"] = "thang máy",
        ["duct"] = "hộp KT",
        ["stair"] = "thang bộ",
    };
}
