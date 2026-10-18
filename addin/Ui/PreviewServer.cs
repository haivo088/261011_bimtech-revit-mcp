using Autodesk.Revit.DB;
using Autodesk.Revit.DB.DirectContext3D;
using Autodesk.Revit.DB.ExternalService;

namespace Bimtech.RevitMcp.Ui;

// Draws where the selected review items will be, before they exist. A DirectContext3D server: the lines live only in
// the graphics, never in the model, so nothing needs a transaction and nothing is left behind when they are cleared.
// Plan views draw everything at the view's cut plane, so the view range never hides it; 3D views draw each item at
// its level. Revit calls the server on its main thread, the same thread that sets and clears the lines.
internal sealed class PreviewServer : IDirectContext3DServer
{
    // One polyline in model feet. Z is the item's level; plan views replace it with their cut plane.
    public sealed record Polyline(IReadOnlyList<XYZ> Points, bool Closed);

    private static readonly Guid ServerId = new("6c1f4a52-3e0b-4d7a-9a51-2f7f3b8de4a1");
    // Orange, unlike anything Revit draws by default.
    private static readonly ColorWithTransparency LineColor = new(255, 120, 0, 0);
    // Indices are 16-bit; keep each buffer well below that.
    private const int MaxVerticesPerBuffer = 30000;

    private Document? _document;
    private IReadOnlyList<Polyline> _lines = [];
    private List<LineBuffer>? _levelled;
    private List<LineBuffer>? _flat;
    private bool _registered;

    public bool HasLines => _lines.Count > 0;

    // Must run in a Revit API context. Registers the server the first time.
    public void Show(Document document, IReadOnlyList<Polyline> lines)
    {
        Register();
        Release();
        (_document, _lines) = (lines.Count == 0 ? null : document, lines);
    }

    public void Clear()
    {
        Release();
        (_document, _lines) = (null, []);
    }

    // True when the lines belong to this document, e.g. the one that is closing.
    public bool Shows(Document document) => _document is not null && _document.Equals(document);

    // Must run in a Revit API context, e.g. OnShutdown.
    public void Unregister()
    {
        Clear();
        if (!_registered) return;
        _registered = false;
        try
        {
            if (ExternalServiceRegistry.GetService(ExternalServices.BuiltInExternalServices.DirectContext3DService) is MultiServerService service
                && service.IsRegisteredServerId(ServerId))
                service.RemoveServer(ServerId);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            // Revit is shutting down; the server goes with it.
        }
    }

    public bool CanExecute(View view) =>
        _document is not null && HasLines && view is ViewPlan or View3D && view.Document.Equals(_document);

    public Outline GetBoundingBox(View view)
    {
        var points = _lines.SelectMany(line => line.Points).ToList();
        if (points.Count == 0) return new Outline(XYZ.Zero, XYZ.Zero);
        var (minZ, maxZ) = view is ViewPlan plan
            ? (CutPlaneZ(plan), CutPlaneZ(plan))
            : (points.Min(p => p.Z), points.Max(p => p.Z));
        return new Outline(new XYZ(points.Min(p => p.X), points.Min(p => p.Y), minZ - 1),
            new XYZ(points.Max(p => p.X), points.Max(p => p.Y), maxZ + 1));
    }

    public bool UseInTransparentPass(View view) => false;

    public void RenderScene(View view, DisplayStyle displayStyle)
    {
        if (!HasLines || DrawContext.IsTransparentPass()) return;
        List<LineBuffer> buffers;
        if (view is ViewPlan plan)
        {
            buffers = _flat ??= Build(_lines, flat: true);
            DrawContext.SetWorldTransform(Transform.CreateTranslation(new XYZ(0, 0, CutPlaneZ(plan))));
        }
        else
        {
            buffers = _levelled ??= Build(_lines, flat: false);
            DrawContext.SetWorldTransform(Transform.Identity);
        }
        foreach (var buffer in buffers)
        {
            if (DrawContext.IsInterrupted()) return;
            DrawContext.FlushBuffer(buffer.Vertices, buffer.VertexCount, buffer.Indices, buffer.IndexCount,
                buffer.Format, buffer.Effect, PrimitiveType.LineList, 0, buffer.IndexCount / 2);
        }
    }

    public bool UsesHandles() => false;
    public Guid GetServerId() => ServerId;
    public ExternalServiceId GetServiceId() => ExternalServices.BuiltInExternalServices.DirectContext3DService;
    public string GetName() => "BIMTECH plan preview";
    public string GetVendorId() => "BIMTECH";
    public string GetDescription() => "Shows where the items of a plan under review will be placed.";
    public string GetApplicationId() => "";
    public string GetSourceId() => "";

    private void Register()
    {
        if (_registered) return;
        if (ExternalServiceRegistry.GetService(ExternalServices.BuiltInExternalServices.DirectContext3DService) is not MultiServerService service)
            return;
        if (!service.IsRegisteredServerId(ServerId)) service.AddServer(this);
        var active = service.GetActiveServerIds();
        if (!active.Contains(ServerId))
        {
            active.Add(ServerId);
            service.SetActiveServers(active);
        }
        _registered = true;
    }

    // The height the plan view cuts at: its cut plane level plus offset, else the view's own level.
    private static double CutPlaneZ(ViewPlan plan)
    {
        var range = plan.GetViewRange();
        var level = plan.Document.GetElement(range.GetLevelId(PlanViewPlane.CutPlane)) as Level ?? plan.GenLevel;
        return (level?.ProjectElevation ?? 0) + range.GetOffset(PlanViewPlane.CutPlane);
    }

    // Line lists in buffers of at most MaxVerticesPerBuffer vertices. Flat buffers put every point at z = 0.
    private static List<LineBuffer> Build(IReadOnlyList<Polyline> lines, bool flat)
    {
        var buffers = new List<LineBuffer>();
        var vertices = new List<XYZ>();
        var indices = new List<(int, int)>();
        foreach (var line in lines)
        {
            if (line.Points.Count < 2) continue;
            if (vertices.Count + line.Points.Count > MaxVerticesPerBuffer)
            {
                buffers.Add(LineBuffer.Create(vertices, indices));
                vertices.Clear();
                indices.Clear();
            }
            var first = vertices.Count;
            vertices.AddRange(line.Points.Select(p => flat ? new XYZ(p.X, p.Y, 0) : p));
            for (var i = 1; i < line.Points.Count; i++) indices.Add((first + i - 1, first + i));
            if (line.Closed && line.Points.Count > 2) indices.Add((first + line.Points.Count - 1, first));
        }
        if (vertices.Count > 0) buffers.Add(LineBuffer.Create(vertices, indices));
        return buffers;
    }

    private void Release()
    {
        foreach (var buffer in (_flat ?? []).Concat(_levelled ?? [])) buffer.Dispose();
        (_flat, _levelled) = (null, null);
    }

    private sealed class LineBuffer(VertexBuffer vertices, int vertexCount, IndexBuffer indices, int indexCount) : IDisposable
    {
        public VertexBuffer Vertices { get; } = vertices;
        public int VertexCount { get; } = vertexCount;
        public IndexBuffer Indices { get; } = indices;
        public int IndexCount { get; } = indexCount;
        public VertexFormat Format { get; } = new(VertexFormatBits.PositionColored);
        public EffectInstance Effect { get; } = new(VertexFormatBits.PositionColored);

        public static LineBuffer Create(IReadOnlyList<XYZ> points, IReadOnlyList<(int A, int B)> lines)
        {
            var vertexFloats = VertexPositionColored.GetSizeInFloats() * points.Count;
            var vertices = new VertexBuffer(vertexFloats);
            vertices.Map(vertexFloats);
            var vertexStream = vertices.GetVertexStreamPositionColored();
            foreach (var point in points) vertexStream.AddVertex(new VertexPositionColored(point, LineColor));
            vertices.Unmap();

            var indexShorts = IndexLine.GetSizeInShortInts() * lines.Count;
            var indices = new IndexBuffer(indexShorts);
            indices.Map(indexShorts);
            var indexStream = indices.GetIndexStreamLine();
            foreach (var (a, b) in lines) indexStream.AddLine(new IndexLine(a, b));
            indices.Unmap();

            return new LineBuffer(vertices, points.Count, indices, indexShorts);
        }

        public void Dispose()
        {
            Vertices.Dispose();
            Indices.Dispose();
            Format.Dispose();
            Effect.Dispose();
        }
    }
}
