using Autodesk.Revit.DB;

namespace Bimtech.RevitMcp.Commands.Common;

// What one write command created, kept in SessionStore under a "batch-N" ID so undo_batch can remove
// exactly these elements later. TypeIds are deleted only when no instance uses them any more.
public sealed record WriteBatch(string Description, IReadOnlyList<ElementId> ElementIds, IReadOnlyList<ElementId> TypeIds)
{
    public const string Kind = "batch";
}
