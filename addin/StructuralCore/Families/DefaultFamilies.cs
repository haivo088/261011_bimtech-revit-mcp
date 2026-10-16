using System.IO;
using Autodesk.Revit.DB;

namespace Bimtech.StructuralCore.Families;

/// <summary>Family chuẩn công ty đóng gói trong lõi (thư mục Families\), nạp vào project khi project chưa có.
/// Chỉ dùng được khi project tiêu thụ bật StructuralCoreEmbedFamilies=true (xem StructuralCore.props) —
/// file .rfa khi đó được nhúng làm EmbeddedResource vào chính assembly gọi hàm này.</summary>
internal static class DefaultFamilies
{
    /// <summary>Family cột bê tông chữ nhật có tham số b/h dạng Length.</summary>
    public const string ColumnFamilyName = "BTC-CotCN_BeTong";

    /// <summary>Family dầm bê tông chữ nhật có tham số b/h dạng Length.</summary>
    public const string BeamFamilyName = "BTC-DamCN_BeTong";

    /// <summary>Family cọc tròn bê tông có tham số Diameter.</summary>
    public const string PileFamilyName = "BTC-CocTron_BeTong";

    private const string ResourcePrefix = "Bimtech.StructuralCore.Families.";

    public static Family? Find(Document doc, string familyName) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(Family))
            .Cast<Family>()
            .FirstOrDefault(f => string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Nạp family từ tài nguyên nhúng trong 1 Transaction riêng và kích hoạt mọi Type — trả về Family đã có
    /// sẵn nếu project đã nạp (không gọi LoadFamily lần 2, tránh Revit hỏi ghi đè). Ném InvalidOperationException kèm
    /// lý do nếu không nạp được.</summary>
    public static Family Load(Document doc, string familyName)
    {
        var existing = Find(doc, familyName);
        if (existing is not null)
            return existing;

        var path = ExtractToTemp(familyName);

        using var tx = new Transaction(doc, $"Nạp family {familyName}");
        tx.Start();
        if (!doc.LoadFamily(path, out var family) || family is null)
        {
            tx.RollBack();
            throw new InvalidOperationException($"Revit không nạp được family từ {path}.");
        }

        foreach (var symbolId in family.GetFamilySymbolIds())
            if (doc.GetElement(symbolId) is FamilySymbol symbol && !symbol.IsActive)
                symbol.Activate();

        tx.Commit();
        return family;
    }

    private static string ExtractToTemp(string familyName)
    {
        var resourceName = ResourcePrefix + familyName + ".rfa";
        using var resource = typeof(DefaultFamilies).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Không tìm thấy tài nguyên {resourceName} — project cần bật StructuralCoreEmbedFamilies=true.");

        // Ten file .rfa quyet dinh ten Family khi nap, nen giu nguyen ten goc; moi lan nap dung thu muc rieng
        // de khong dung file dang bi Revit khac giu.
        var directory = Path.Combine(Path.GetTempPath(), "BIMTech", "StructuralCore", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, familyName + ".rfa");
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        resource.CopyTo(output);
        return path;
    }
}
