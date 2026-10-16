using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace Bimtech.StructuralCore.Beams;

/// <summary>Ghi dầm cho action create_beams (propose_beams): Type đúng b×h, đặt 1 dầm, chống trùng dầm đã có.
/// Nhận diện dầm nằm ở addin/Recognition (BeamRecognizer), không ở đây.</summary>
internal static class BeamPlacer
{
    private const double DuplicatePositionToleranceMm = 10.0;
    private const double DuplicateAngleToleranceDegrees = 1.0;

    internal readonly record struct BeamLine(XYZ Start, XYZ End);

    /// <summary>Type dầm đúng b×h trong Family mẫu, trong Transaction do caller mở sẵn: dùng lại nếu có (kể cả Type vừa tạo
    /// ở mục trước), không thì duplicate Type gần nhất, tên "b×h", set b/h. Ném InvalidOperationException khi Family không
    /// có tham số b/h dạng Length sửa được.</summary>
    public static (FamilySymbol Symbol, bool Created) ResolveOrCreateBeamType(Document doc, double bMm, double hMm, Family family)
    {
        var symbols = family.GetFamilySymbolIds().Select(doc.GetElement).OfType<FamilySymbol>().ToList();
        if (BeamTypeMatcher.FindExactMatch(bMm, hMm, symbols, s => s) is { } existing)
            return (existing, false);
        var template = BeamTypeMatcher.AutoMatchExact(bMm, hMm, symbols, s => s)
            ?? throw new InvalidOperationException($"Family \"{family.Name}\" không có Type chứa tham số b/h dạng Length.");
        var name = SymbolParameters.UniqueName($"{(long)Math.Round(bMm)}x{(long)Math.Round(hMm)}",
            symbols.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
        if (template.Duplicate(name) is not FamilySymbol created)
            throw new InvalidOperationException("Duplicate không trả về FamilySymbol.");
        var b = created.LookupParameter(BeamTypeMatcher.BeamBParam);
        var h = created.LookupParameter(BeamTypeMatcher.BeamHParam);
        if (b is not { StorageType: StorageType.Double, IsReadOnly: false } || h is not { StorageType: StorageType.Double, IsReadOnly: false })
            throw new InvalidOperationException($"Family \"{family.Name}\" không có tham số b/h dạng Length sửa được.");
        b.Set(UnitUtils.ConvertToInternalUnits(bMm, UnitTypeId.Millimeters));
        h.Set(UnitUtils.ConvertToInternalUnits(hMm, UnitTypeId.Millimeters));
        return (created, true);
    }

    /// <summary>Đặt 1 dầm từ start tới end (Z đã gồm cao độ đỉnh dầm), trong Transaction do caller mở sẵn; ghi mark vào
    /// Mark nếu có. Revit ném lỗi khi không tạo được.</summary>
    public static ElementId PlaceBeamInTransaction(Document doc, XYZ start, XYZ end, FamilySymbol symbol, Level level, string? mark)
    {
        if (!symbol.IsActive) symbol.Activate();
        var instance = doc.Create.NewFamilyInstance(Line.CreateBound(start, end), symbol, level, StructuralType.Beam);
        if (mark is not null) instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(mark);
        return instance.Id;
    }

    /// <summary>Trục mọi dầm thẳng đã có trong model, để chống đặt trùng.</summary>
    internal static List<BeamLine> CollectExistingBeamLines(Document doc) =>
        new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_StructuralFraming)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .Where(fi => fi.StructuralType == StructuralType.Beam && fi.Location is LocationCurve { Curve: Line })
            .Select(fi => (Line)((LocationCurve)fi.Location).Curve)
            .Select(line => new BeamLine(line.GetEndPoint(0), line.GetEndPoint(1)))
            .ToList();

    /// <summary>Trùng khi cùng cao độ, cùng phương (lệch ≤ 1°), cùng trục (lệch ≤ 10 mm) và chồng lên nhau dọc trục.</summary>
    internal static bool AreDuplicate(BeamLine existing, BeamLine candidate)
    {
        var toleranceFeet = UnitUtils.ConvertToInternalUnits(DuplicatePositionToleranceMm, UnitTypeId.Millimeters);
        if (Math.Abs(existing.Start.Z - candidate.Start.Z) > toleranceFeet ||
            Math.Abs(existing.End.Z - candidate.End.Z) > toleranceFeet)
            return false;

        var existingVector = new XYZ(existing.End.X - existing.Start.X, existing.End.Y - existing.Start.Y, 0);
        var candidateVector = new XYZ(candidate.End.X - candidate.Start.X, candidate.End.Y - candidate.Start.Y, 0);
        var existingLength = existingVector.GetLength();
        var candidateLength = candidateVector.GetLength();
        if (existingLength <= toleranceFeet || candidateLength <= toleranceFeet)
            return false;

        var existingDirection = existingVector / existingLength;
        var candidateDirection = candidateVector / candidateLength;
        var cross = Math.Abs(existingDirection.X * candidateDirection.Y - existingDirection.Y * candidateDirection.X);
        if (cross > Math.Sin(DuplicateAngleToleranceDegrees * Math.PI / 180.0))
            return false;

        double DistanceToExistingAxis(XYZ point)
        {
            var offsetX = point.X - existing.Start.X;
            var offsetY = point.Y - existing.Start.Y;
            return Math.Abs(offsetX * existingDirection.Y - offsetY * existingDirection.X);
        }

        if (DistanceToExistingAxis(candidate.Start) > toleranceFeet ||
            DistanceToExistingAxis(candidate.End) > toleranceFeet)
            return false;

        double ProjectOnExistingAxis(XYZ point) =>
            (point.X - existing.Start.X) * existingDirection.X +
            (point.Y - existing.Start.Y) * existingDirection.Y;

        var candidateMin = Math.Min(ProjectOnExistingAxis(candidate.Start), ProjectOnExistingAxis(candidate.End));
        var candidateMax = Math.Max(ProjectOnExistingAxis(candidate.Start), ProjectOnExistingAxis(candidate.End));
        var overlap = Math.Min(existingLength, candidateMax) - Math.Max(0.0, candidateMin);
        return overlap > toleranceFeet;
    }
}
