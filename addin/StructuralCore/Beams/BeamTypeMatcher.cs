using Autodesk.Revit.DB;

namespace Bimtech.StructuralCore.Beams;

/// <summary>Khớp Type dầm theo tham số b/h của Type. Khác cột: KHÔNG hoán đổi b/h, vì thứ tự "bxh" trong Text đã rõ.</summary>
internal static class BeamTypeMatcher
{
    /// <summary>Tên Parameter bề rộng/chiều cao tiết diện — mặc định theo quy ước phổ biến của Revit (giống
    /// PlaceColumn), CHƯA đối chiếu với Family dầm chuẩn công ty (cần verify trên Revit thật). "h" chỉ dùng
    /// để auto-match khi đọc được từ Text (AutoMatchExact) — không đo được từ mặt bằng CAD nên
    /// nhóm gộp theo hình học (không có tên) vẫn chỉ so khớp theo b (AutoMatchByWidth).</summary>
    public const string BeamBParam = "b";
    public const string BeamHParam = "h";

    /// <summary>Dung sai (mm) coi Type là khớp ĐÚNG (b, h) đọc từ Text, giống hệt ngưỡng PlaceColumn dùng cho cột.</summary>
    public const double MatchToleranceMm = 2.0;

    /// <summary>Mọi Type trong category Structural Framing của project.</summary>
    public static List<FamilySymbol> CollectBeamTypes(Document doc) =>
        new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_StructuralFraming)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .ToList();

    /// <summary>Type có bề rộng "b" gần đúng nhất với bề rộng nhóm dầm — không so "h" vì không đọc được từ
    /// mặt bằng CAD. Null nếu không Type nào có parameter "b".</summary>
    public static T? AutoMatchByWidth<T>(double sizeBMm, IEnumerable<T> choices, Func<T, FamilySymbol> symbolOf)
        where T : class
    {
        T? best = null;
        var bestDiff = double.MaxValue;

        foreach (var choice in choices)
        {
            var b = SymbolParameters.ReadLengthMm(symbolOf(choice), BeamBParam);
            if (b is null)
                continue;

            var diff = Math.Abs(b.Value - sizeBMm);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = choice;
            }
        }

        return best;
    }

    /// <summary>Type có (b, h) gần đúng nhất với kích thước đọc được từ Text — so cả 2 chiều, không hoán đổi.
    /// Null nếu không Type nào có đủ cả 2 parameter "b"/"h".</summary>
    public static T? AutoMatchExact<T>(double bMm, double hMm, IEnumerable<T> choices, Func<T, FamilySymbol> symbolOf)
        where T : class
    {
        T? best = null;
        var bestDiff = double.MaxValue;

        foreach (var choice in choices)
        {
            var symbol = symbolOf(choice);
            var b = SymbolParameters.ReadLengthMm(symbol, BeamBParam);
            var h = SymbolParameters.ReadLengthMm(symbol, BeamHParam);
            if (b is null || h is null)
                continue;

            var diff = Math.Abs(b.Value - bMm) + Math.Abs(h.Value - hMm);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = choice;
            }
        }

        return best;
    }

    /// <summary>Type có (b, h) khớp ĐÚNG (trong dung sai MatchToleranceMm) với kích thước đọc từ Text.</summary>
    public static bool IsMatch(FamilySymbol symbol, double bMm, double hMm)
    {
        var b = SymbolParameters.ReadLengthMm(symbol, BeamBParam);
        var h = SymbolParameters.ReadLengthMm(symbol, BeamHParam);
        if (b is null || h is null)
            return false;

        return Math.Abs(b.Value - bMm) <= MatchToleranceMm && Math.Abs(h.Value - hMm) <= MatchToleranceMm;
    }

    public static T? FindExactMatch<T>(double bMm, double hMm, IEnumerable<T> choices, Func<T, FamilySymbol> symbolOf)
        where T : class =>
        choices.FirstOrDefault(choice => IsMatch(symbolOf(choice), bMm, hMm));
}
