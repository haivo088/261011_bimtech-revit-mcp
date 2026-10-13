using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Protocol;

namespace Bimtech.RevitMcp.Commands;

// State that must survive between tool calls but is too large or too precise to send to Claude:
// CAD scan results (thousands of points) and the elements each write created.
// Claude only receives short IDs such as "scan-3" and "batch-2". Everything lives in memory and is
// lost when the bridge stops. All access happens on Revit's API thread, so no locking is needed.
public sealed class SessionStore
{
    public const int MaxEntriesPerKind = 20;

    private readonly Dictionary<string, (Document Document, object Value)> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<string>> _orderByKind = new(StringComparer.Ordinal);
    private int _nextId = 1;

    // Stores a value tied to a document and returns its ID, e.g. "scan-1". Drops the oldest entry of the kind when full.
    public string Add<T>(string kind, Document document, T value) where T : class
    {
        if (!_orderByKind.TryGetValue(kind, out var order))
            _orderByKind[kind] = order = new Queue<string>();
        while (order.Count >= MaxEntriesPerKind) _entries.Remove(order.Dequeue());

        var id = $"{kind}-{_nextId++}";
        _entries[id] = (document, value);
        order.Enqueue(id);
        return id;
    }

    // Returns the value only for the same open document, so a scan of model A is never applied to model B.
    public T Require<T>(string kind, string id, Document document, string paramName) where T : class
    {
        if (!_entries.TryGetValue(id, out var entry) || entry.Value is not T value || !id.StartsWith(kind + "-", StringComparison.Ordinal))
            throw new RpcException(RpcErrorCodes.SessionExpired,
                $"Unknown {kind} ID '{id}'. It was already used up (e.g. undone), expired, or the bridge was restarted.",
                new { param = paramName });
        if (!entry.Document.IsValidObject || !entry.Document.Equals(document))
            throw new RpcException(RpcErrorCodes.SessionExpired,
                $"{kind} '{id}' belongs to another document. Run the {kind} step again in this model.",
                new { param = paramName });
        return value;
    }

    public void Remove(string id) => _entries.Remove(id);

    // Replaces the value of an existing entry, keeping its ID and document.
    public void Replace<T>(string id, T value) where T : class
    {
        if (_entries.TryGetValue(id, out var entry)) _entries[id] = (entry.Document, value);
    }
}
