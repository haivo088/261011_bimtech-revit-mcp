namespace Bimtech.RevitMcp.Data;

// Vietnamese text shown to the reviewer for grids from data. The only file of Data/ with non-English text.
internal static class GridDataText
{
    public const string Vertical = "đứng";
    public const string Horizontal = "ngang";
    public const string Summary = "Trục {0} ({1}): {2} = {3} mm từ gốc{4}";
    public const string SummaryAux = " · trục phụ";
    public const string SummaryFix = " · tên do người dùng đặt";

    public const string FromData = "Từ dữ liệu {0} ({1}, hash {2}): đã kiểm tra dim, dãy tên, đơn vị trước khi dựng.";
    public const string CadHandle = "Đối tượng CAD: {0} (bubble {1}).";
    public const string NamedByFix = "Tên do người dùng đặt trong {0}: bản vẽ không có bubble cho trục này.";
    public const string Unnamed = "Trục chưa có tên trong dữ liệu: đặt tên trong file _fix.json rồi xuất lại.";
    public const string NameInModel = "Model đã có Grid tên '{0}'.";
    public const string NameTwice = "Dữ liệu có hơn một trục tên '{0}'.";
    public const string SelfCheckFailed = "Dữ liệu không qua bước tự kiểm tra (stats.self_check = false): xem lại trước khi dựng.";
    public const string NoSelfCheck = "Dữ liệu không có kết quả tự kiểm tra (stats.self_check).";

    // Written to the grid's Comments in the model: schema, source file, hash, CAD handle.
    public const string Trace = "{0} · {1} #{2} · CAD {3}";

    public const string Title = "Dựng {0} trục từ dữ liệu {1}";
}
