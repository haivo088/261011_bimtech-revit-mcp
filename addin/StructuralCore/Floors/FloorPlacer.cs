using Autodesk.Revit.DB;

namespace Bimtech.StructuralCore.Floors;

/// <summary>1 sàn cần đặt: biên ngoài (curve phẳng, Z bất kỳ), các lỗ mở, Floor Type, Level, offset so với Level (mm).
/// Label dùng trong thông báo lỗi, vd "Sàn #3".</summary>
internal sealed record FloorPlacement(
    string Label, List<Curve> OuterLoop, IReadOnlyList<List<Curve>> Holes, FloorType? FloorType, Level? Level, double OffsetMm);

/// <summary>Đặt toàn bộ sàn (Floor.Create outer + hole, structural) vào model trong 1 Transaction — dùng đúng Floor Type người dùng đã chọn (không tự so khớp/tạo theo độ dày); cứ tạo bình thường rồi tận dụng ElementIntersectsElementFilter (kiểm tra giao nhau hình học thật của Revit) để phát hiện Floor vừa tạo chồng lắp Floor nào đã có, phát hiện là xoá luôn Floor vừa tạo.</summary>
internal static class FloorPlacer
{
    public static PlaceSummary Place(Document doc, IReadOnlyList<FloorPlacement> placements)
    {
        using var tx = new Transaction(doc, "Vẽ Sàn theo Dầm");
        tx.Start();
        var summary = PlaceInTransaction(doc, placements);
        tx.Commit();
        return summary;
    }

    /// <summary>Như Place nhưng chạy trong Transaction do caller mở sẵn (khung plan của MCP: mỗi mục 1 Transaction).</summary>
    public static PlaceSummary PlaceInTransaction(Document doc, IReadOnlyList<FloorPlacement> placements)
    {
        var created = 0;
        var skipped = 0;
        var failed = 0;
        var errors = new List<string>();
        var createdIds = new List<ElementId>();

        foreach (var row in placements)
        {
            if (row.FloorType is not FloorType floorType)
            {
                failed++;
                errors.Add($"{row.Label}: chưa chọn Floor Type, bỏ qua.");
                continue;
            }

            if (row.Level is not Level level)
            {
                failed++;
                errors.Add($"{row.Label}: chưa chọn Level, bỏ qua.");
                continue;
            }

            try
            {
                var outerPoints = PolygonMath.Tessellate(row.OuterLoop);
                var outerSign = Math.Sign(PolygonMath.SignedArea(outerPoints));

                var loops = new List<CurveLoop> { BuildLoopAtZ(row.OuterLoop, level.Elevation) };
                foreach (var hole in row.Holes)
                {
                    // Hole phai nguoc chieu winding voi outer (quy uoc profile co lo mo chuan) - dao ca thu tu
                    // curve lan chieu tung curve neu vong pick duoc cung chieu voi outer.
                    var holeSign = Math.Sign(PolygonMath.SignedArea(PolygonMath.Tessellate(hole)));
                    var orientedHole = holeSign == outerSign && holeSign != 0 ? ReverseLoop(hole) : hole;
                    loops.Add(BuildLoopAtZ(orientedHole, level.Elevation));
                }

                var floor = Floor.Create(doc, loops, floorType.Id, level.Id, true, null, 0.0);

                var offsetFeet = UnitUtils.ConvertToInternalUnits(row.OffsetMm, UnitTypeId.Millimeters);
                floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.Set(offsetFeet);

                doc.Regenerate();

                var overlapsExisting = new FilteredElementCollector(doc)
                    .OfClass(typeof(Floor))
                    .WherePasses(new ElementIntersectsElementFilter(floor))
                    .Cast<Floor>()
                    .Any(f => f.Id != floor.Id);

                if (overlapsExisting)
                {
                    doc.Delete(floor.Id);
                    skipped++;
                    continue;
                }

                createdIds.Add(floor.Id);
                created++;
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"{row.Label}: {ex.Message}");
            }
        }

        return new PlaceSummary(created, failed, skipped, errors, createdIds);
    }

    /// <summary>Đảo cả thứ tự curve lẫn chiều từng curve — dùng khi hole cùng chiều winding với outer, cần đảo lại cho đúng quy ước profile-với-lỗ-mở.</summary>
    private static List<Curve> ReverseLoop(List<Curve> curves)
    {
        var reversed = new List<Curve>(curves.Count);
        for (var i = curves.Count - 1; i >= 0; i--)
            reversed.Add(curves[i].CreateReversed());

        return reversed;
    }

    /// <summary>Dịch cả loop về đúng cao độ Z (elevation của Level đã chọn) — curve gốc luôn phẳng Z=0 sau khi chaining/đọc Face.</summary>
    private static CurveLoop BuildLoopAtZ(List<Curve> curves, double z)
    {
        var loop = new CurveLoop();
        foreach (var curve in curves)
        {
            var translate = Transform.CreateTranslation(new XYZ(0, 0, z - curve.GetEndPoint(0).Z));
            loop.Append(curve.CreateTransformed(translate));
        }

        return loop;
    }
}
