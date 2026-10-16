using Autodesk.Revit.DB;
using Bimtech.StructuralCore.Cad;
using View = Autodesk.Revit.DB.View;

namespace Bimtech.StructuralCore.Columns;

/// <summary>Đọc đường bao cột chữ nhật (có thể xoay) từ 1 ImportInstance, gộp theo layer — nhận cả PolyLine
/// (khép kín hoặc hở) lẫn cột được người vẽ CAD tách thành nhiều Line rời rạc từng cạnh một.</summary>
internal static class ColumnScanner
{
    private static readonly double MinSizeFeet = UnitUtils.ConvertToInternalUnits(5.0, UnitTypeId.Millimeters);

    /// <summary>Ngưỡng coi 2 đầu Line là "nối nhau" khi gộp cụm Line rời rạc thành 1 đường bao.</summary>
    private static readonly double ConnectTolFeet = UnitUtils.ConvertToInternalUnits(2.0, UnitTypeId.Millimeters);

    private const int MaxRecursionDepth = 8;

    public static ColumnScanResult Scan(Document doc, ImportInstance instance, View view)
    {
        var columnsByLayer = new Dictionary<string, List<RawColumn>>(StringComparer.Ordinal);
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
            Walk(doc, geometryElement, columnsByLayer, linesByLayer, 0);

        // Cot duoc ve bang nhieu Line roi rac tung canh (khong noi thanh 1 PolyLine) - gop cac Line
        // cung layer co dau noi gan nhau thanh 1 cum, moi cum du diem thi quy ve hinh chu nhat y het
        // duong PolyLine ben tren.
        foreach (var (layerName, segments) in linesByLayer)
            AddLineClusters(layerName, segments, columnsByLayer);

        return new ColumnScanResult { ColumnsByLayer = columnsByLayer };
    }

    private static void Walk(
        Document doc, GeometryElement geometryElement,
        Dictionary<string, List<RawColumn>> columnsByLayer,
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
                            Walk(doc, nested, columnsByLayer, linesByLayer, depth + 1);
                        break;

                    case PolyLine polyLine:
                        AddPolyLine(doc, polyLine, columnsByLayer);
                        break;

                    case Line line:
                        AddLine(doc, line, linesByLayer);
                        break;
                }
            }
            catch (Exception)
            {
                // Bo qua rieng 1 doi tuong loi, khong sap ca luot quet.
            }
        }
    }

    /// <summary>Nhận mọi PolyLine (khép kín hoặc hở, tối thiểu 3 điểm) — quy về hình chữ nhật xoay nhỏ nhất bao quanh.</summary>
    private static void AddPolyLine(Document doc, PolyLine polyLine, Dictionary<string, List<RawColumn>> columnsByLayer)
    {
        var raw = polyLine.GetCoordinates();
        if (raw.Count < 3)
            return;

        var flat = raw.Select(p => new XYZ(p.X, p.Y, 0)).ToList();

        var rect = CadGeometry.MinAreaRectangle(flat);
        if (rect is null)
            return;

        var (center, lengthFeet, widthFeet, angleRadians) = rect.Value;
        if (lengthFeet < MinSizeFeet || widthFeet < MinSizeFeet)
            return;

        var layerName = CadGeometry.ResolveLayerName(doc, polyLine.GraphicsStyleId);
        var segments = new List<(XYZ, XYZ)>();
        for (var i = 0; i < flat.Count - 1; i++)
            segments.Add((flat[i], flat[i + 1]));

        AddColumn(columnsByLayer, layerName, center, lengthFeet, widthFeet, angleRadians, segments);
    }

    /// <summary>Gom 1 Line CAD thô (Z=0) vào danh sách theo layer — xử lý gộp cụm dồn lại sau khi Walk xong toàn bộ hình học.</summary>
    private static void AddLine(Document doc, Line line, Dictionary<string, List<(XYZ A, XYZ B)>> linesByLayer)
    {
        XYZ p0, p1;
        try
        {
            p0 = line.GetEndPoint(0);
            p1 = line.GetEndPoint(1);
        }
        catch (Exception)
        {
            return;
        }

        var a = new XYZ(p0.X, p0.Y, 0);
        var b = new XYZ(p1.X, p1.Y, 0);
        if (a.DistanceTo(b) < 1e-9)
            return;

        var layerName = CadGeometry.ResolveLayerName(doc, line.GraphicsStyleId);
        if (!linesByLayer.TryGetValue(layerName, out var list))
        {
            list = new List<(XYZ, XYZ)>();
            linesByLayer[layerName] = list;
        }

        list.Add((a, b));
    }

    /// <summary>Gộp các Line rời rạc cùng layer thành từng cụm nối liền nhau (2 đầu gần nhau trong ConnectTolFeet
    /// coi là nối) — mỗi cụm đủ điểm thì quy về hình chữ nhật xoay nhỏ nhất y hệt đường PolyLine. Độ phức tạp
    /// O(n²) chấp nhận được vì mỗi layer cột chỉ vài trăm đến vài nghìn Line.</summary>
    private static void AddLineClusters(string layerName, List<(XYZ A, XYZ B)> segments, Dictionary<string, List<RawColumn>> columnsByLayer)
    {
        var used = new bool[segments.Count];

        for (var i = 0; i < segments.Count; i++)
        {
            if (used[i])
                continue;

            var clusterIdx = new List<int> { i };
            used[i] = true;

            var changed = true;
            while (changed)
            {
                changed = false;
                for (var j = 0; j < segments.Count; j++)
                {
                    if (used[j])
                        continue;

                    var (a, b) = segments[j];
                    var connects = clusterIdx.Exists(k =>
                    {
                        var (ka, kb) = segments[k];
                        return a.DistanceTo(ka) <= ConnectTolFeet || a.DistanceTo(kb) <= ConnectTolFeet ||
                               b.DistanceTo(ka) <= ConnectTolFeet || b.DistanceTo(kb) <= ConnectTolFeet;
                    });

                    if (!connects)
                        continue;

                    clusterIdx.Add(j);
                    used[j] = true;
                    changed = true;
                }
            }

            var clusterSegments = clusterIdx.Select(k => segments[k]).ToList();
            var points = clusterSegments.SelectMany(s => new[] { s.A, s.B }).ToList();
            if (points.Count < 3)
                continue; // 1 Line don le (2 diem) - khong du xac dinh ca 2 canh hinh chu nhat, bo qua.

            var rect = CadGeometry.MinAreaRectangle(points);
            if (rect is null)
                continue;

            var (center, lengthFeet, widthFeet, angleRadians) = rect.Value;
            if (lengthFeet < MinSizeFeet || widthFeet < MinSizeFeet)
                continue;

            AddColumn(columnsByLayer, layerName, center, lengthFeet, widthFeet, angleRadians, clusterSegments);
        }
    }

    private static void AddColumn(
        Dictionary<string, List<RawColumn>> columnsByLayer, string layerName,
        XYZ center, double lengthFeet, double widthFeet, double angleRadians, List<(XYZ A, XYZ B)> segments)
    {
        if (!columnsByLayer.TryGetValue(layerName, out var list))
        {
            list = new List<RawColumn>();
            columnsByLayer[layerName] = list;
        }

        list.Add(new RawColumn
        {
            Center = center,
            LengthFeet = lengthFeet,
            WidthFeet = widthFeet,
            AngleRadians = angleRadians,
            Segments = segments,
        });
    }
}
