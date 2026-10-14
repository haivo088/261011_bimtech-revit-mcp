using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;

namespace Bimtech.RevitMcp.Planning;

// Counts DocumentChanged events per open model for this Revit session, so commit_plan can tell
// whether the model changed after a plan was proposed (docs/04-architecture.md, principle 3).
// Every change counts: the person's edits, undo/redo, other add-ins, and other plans' commits.
// Subscribed in App.OnStartup and kept for the whole session.
public sealed class ModelChangeTracker : IDisposable
{
    private readonly ControlledApplication _application;
    private readonly Dictionary<string, long> _counters = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public ModelChangeTracker(ControlledApplication application)
    {
        _application = application;
        _application.DocumentChanged += OnDocumentChanged;
    }

    // New for every Revit session, so a counter from an earlier session never matches.
    public string SessionId { get; } = Guid.NewGuid().ToString("N");

    public ModelStamp Current(Document document)
    {
        lock (_lock)
            return new ModelStamp(SessionId, _counters.GetValueOrDefault(KeyOf(document)));
    }

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        var key = KeyOf(e.GetDocument());
        lock (_lock) _counters[key] = _counters.GetValueOrDefault(key) + 1;
    }

    // Same identity as PlanStorageLocation.IsSameModel: path (case-insensitive) and title.
    private static string KeyOf(Document document) =>
        (document.PathName ?? "").ToUpperInvariant() + "|" + document.Title;

    public void Dispose() => _application.DocumentChanged -= OnDocumentChanged;
}
