namespace Bimtech.RevitMcp.Data;

// Vietnamese text shared by every "from data" command (grid origin check). The only non-English text of this file.
internal static class DataText
{
    public const string OriginProblem = "Lưới trục trong model không khớp gốc dữ liệu: {0}. Cấu kiện sẽ lệch nếu ghi.";
    public const string OriginMissing = "model chưa có trục {0}";
    public const string OriginOff = "trục {0} cách Project Base Point {1} mm";
}
