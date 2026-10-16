using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Recognition;
using Bimtech.StructuralCore.Cad;
using View = Autodesk.Revit.DB.View;

namespace Bimtech.StructuralCore.Columns;

/// <summary>Đọc đường bao cột chữ nhật (có thể xoay) từ 1 ImportInstance, gộp theo layer — nhận cả PolyLine lẫn cột được
/// người vẽ CAD tách thành nhiều Line rời từng cạnh. Chỉ nhận hình THẬT SỰ là chữ nhật (Shapes.IsRectangle) với cạnh trong
/// khoảng cột: mũi tên, nét ghi chú, cột chữ L hay cột dính vách cùng layer không thành "cột" mà được liệt kê riêng.</summary>
internal static class ColumnScanner
{
    public const double MinSideMm = 100.0;
    public const double MaxSideMm = 2000.0;

    /// <summary>Ngưỡng coi 2 đầu Line là "nối nhau" khi gộp cụm Line rời rạc thành 1 đường bao.</summary>
    private const double ConnectTolMm = 2.0;

    private const int MaxRecursionDepth = 8;

    public static ColumnScanResult Scan(Document doc, ImportInstance instance, View view)
    {
        var columnsByLayer = new Dictionary<string, List<RawColumn>>(StringComparer.Ordinal);
        var rejectedByLayer = new Dictionary<string, List<RejectedShape>>(StringComparer.Ordinal);
        var linesByLayer = new Dictionary<string, List<(XYZ A, XYZ B)>>(StringComparer.Ordinal);

        var options = new Options { View = view, IncludeNonVisibleObjects = false };
        GeometryElement? geometryElement;
        try
        {
            geometryElement = instance.get_Geometry(options);
        }
        catch (Exception)
        {
            geometryElement = null;
        }

        if (geometryElement is not null)
            Walk(doc, geometryElement, columnsByLayer, rejectedByLayer, linesByLayer, 0);

        // Cột vẽ bằng nhiều Line rời từng cạnh: gom các Line cùng layer có đầu chạm nhau thành cụm (lưới không gian,
        // không so từng cặp), mỗi cụm quy về hình chữ nhật y hệt PolyLine.
        foreach (var (layerName, segments) in linesByLayer)
        {
            var mm = segments.Select(s => ToMm(s.A, s.B)).ToList();
            foreach (var cluster in Shapes.Clusters(mm, ConnectTolMm))
            {
                if (cluster.Count < 2) continue; // 1 Line lẻ không xác định được hình.
                AddShape(layerName, cluster.Select(i => segments[i]).ToList(), columnsByLayer, rejectedByLayer);
            }
        }

        return new ColumnScanResult { ColumnsByLayer = columnsByLayer, RejectedByLayer = rejectedByLayer };
    }

    private static void Walk(
        Document doc, GeometryElement geometryElement,
        Dictionary<string, List<RawColumn>> columnsByLayer, Dictionary<string, List<RejectedShape>> rejectedByLayer,
        Dictionary<string, List<(XYZ A, XYZ B)>> linesByLayer, int depth)
    {
        if (depth > MaxRecursionDepth)
            return;

        foreach (var obj in geometryElement)
        {
            try
            {
                switch (obj)
                {
                    case GeometryInstance instance:
                        var nested = instance.GetInstanceGeometry();
                        if (nested is not null)
                            Walk(doc, nested, columnsByLayer, rejectedByLayer, linesByLayer, depth + 1);
                        break;

                    case PolyLine polyLine:
                        var points = polyLine.GetCoordinates().Select(Flat).ToList();
                        if (points.Count < 3) break;
                        // Nối điểm cuối về điểm đầu: PolyLine hở thiếu 1 cạnh vẫn thành chữ nhật; mũi tên 2 cạnh thì không.
                        if (points[0].DistanceTo(points[^1]) > 1e-9) points.Add(points[0]);
                        var segments = points.Zip(points.Skip(1)).Where(p => p.First.DistanceTo(p.Second) > 1e-9).ToList();
                        AddShape(CadGeometry.ResolveLayerName(doc, polyLine.GraphicsStyleId), segments, columnsByLayer, rejectedByLayer);
                        break;

                    case Line line:
                        var (a, b) = (Flat(line.GetEndPoint(0)), Flat(line.GetEndPoint(1)));
                        if (a.DistanceTo(b) < 1e-9) break;
                        var layer = CadGeometry.ResolveLayerName(doc, line.GraphicsStyleId);
                        if (!linesByLayer.TryGetValue(layer, out var list)) linesByLayer[layer] = list = [];
                        list.Add((a, b));
                        break;
                }
            }
            catch (Exception)
            {
                // Bo qua rieng 1 doi tuong loi, khong sap ca luot quet.
            }
        }
    }

    /// <summary>1 hình (PolyLine hoặc cụm Line): thành cột nếu là chữ nhật có cạnh trong khoảng cột; hình có kích thước cỡ cột
    /// mà không phải chữ nhật (cột chữ L, cột dính vách) được ghi lại để báo; hình quá nhỏ/quá lớn (ký hiệu, vách) bỏ qua.</summary>
    private static void AddShape(
        string layerName, List<(XYZ A, XYZ B)> segments,
        Dictionary<string, List<RawColumn>> columnsByLayer, Dictionary<string, List<RejectedShape>> rejectedByLayer)
    {
        var rect = CadGeometry.MinAreaRectangle(segments.SelectMany(s => new[] { s.A, s.B }).ToList());
        if (rect is null) return;
        var (center, lengthFeet, widthFeet, angleRadians) = rect.Value;
        var (lengthMm, widthMm) = (Mm(lengthFeet), Mm(widthFeet));
        // Narrower than a column: a mark or a symbol, not worth reporting.
        if (Math.Min(lengthMm, widthMm) < MinSideMm || Math.Min(lengthMm, widthMm) > MaxSideMm) return;

        string? reason = null;
        if (Math.Max(lengthMm, widthMm) > MaxSideMm)
            reason = $"Dài {Math.Max(lengthMm, widthMm):0} mm, quá {MaxSideMm:0} mm của cột: vách, hoặc cột vẽ dính vách cùng layer.";
        else if (!Shapes.IsRectangle(segments.Select(s => ToMm(s.A, s.B)).ToList(),
                     new Shapes.Rectangle(Mm(center.X), Mm(center.Y), lengthMm, widthMm, angleRadians)))
            reason = "Không phải hình chữ nhật (cột chữ L, cột dính nét khác, hay nét hở).";
        if (reason is not null)
        {
            if (!rejectedByLayer.TryGetValue(layerName, out var rejected)) rejectedByLayer[layerName] = rejected = [];
            rejected.Add(new RejectedShape(center, lengthMm, widthMm, reason));
            return;
        }

        if (!columnsByLayer.TryGetValue(layerName, out var list)) columnsByLayer[layerName] = list = [];
        list.Add(new RawColumn
        {
            Center = center,
            LengthFeet = lengthFeet,
            WidthFeet = widthFeet,
            AngleRadians = angleRadians,
            Segments = segments,
        });
    }

    private static XYZ Flat(XYZ p) => new(p.X, p.Y, 0);

    private static double Mm(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);

    private static Shapes.Segment ToMm(XYZ a, XYZ b) => new(Mm(a.X), Mm(a.Y), Mm(b.X), Mm(b.Y));
}
