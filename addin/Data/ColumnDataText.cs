namespace Bimtech.RevitMcp.Data;

// Vietnamese text shown to the reviewer for columns from data. The only file of Data/ columns with non-English text.
internal static class ColumnDataText
{
    public const string Summary = "{0} · {1} cột · {2}";
    public const string SummaryPlanted = "{0} · {1} cột cấy · {2}";
    public const string Title = "Dựng {0} cột {1} → {2} từ dữ liệu {3}";

    public const string FromData = "Từ dữ liệu {0} ({1}, hash {2}), vị trí theo lưới {3}.";
    public const string Checked = "Đã kiểm tra ngoài Revit: đường bao chữ nhật, không trùng, không chồng nhau, kích thước block = hình vẽ ({0}/{1}).";
    public const string NotChecked = "Dữ liệu chưa qua đủ kiểm tra (chữ nhật, trùng, chồng nhau, kích thước block = hình vẽ {0}/{1}): xem lại.";
    public const string NewType = "Sẽ tạo Type mới: kiểm tra family và kích thước trước khi ghi.";
    public const string UnknownKind = "{0} cột không rõ loại (thấy/khuất/cấy): kiểm tra trước khi dựng.";
    public const string Planted = "Cột cấy (mọc từ dầm, không có cột bên dưới): kiểm tra dầm đỡ trước khi dựng.";
    public const string Existing = "{0} cột đã có trong model tại đó (mọi family), bỏ qua.";
    public const string At = "Vị trí: {0}";
    public const string TypeInFamily = "Type {0} trong {1}";
    public const string NoType = "chưa có Type";
    public const string NoTypeProblem = "Chưa có Type cột {0}: gán trong 'types', truyền templateFamilyId hoặc load_default_family kind=column.";
}
