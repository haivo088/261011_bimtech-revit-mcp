using System.Text.Json;
using Autodesk.Revit.DB;

namespace Bimtech.RevitMcp.Planning.Actions;

// How one kind of plan item is written into the model, e.g. "set_parameter".
// Apply runs inside an open Transaction that RevitPlanWriter owns: it must not start its own.
// Return a JSON-serializable result (element IDs created, old and new values...);
// throw PlanActionException for an expected failure, which becomes the item's error message.
public interface IPlanAction
{
    // Must match PlanItem.Action written by the propose command.
    string Action { get; }

    object Apply(Document document, JsonElement parameters);
}

// An item that cannot be written, with a message the reviewer and Claude can read.
public sealed class PlanActionException(string message) : Exception(message);

// Every action the executor knows. A propose command may only create items whose action is listed here.
public sealed class PlanActions
{
    private readonly Dictionary<string, IPlanAction> _actions = new(StringComparer.Ordinal);

    public PlanActions(IEnumerable<IPlanAction> actions)
    {
        foreach (var action in actions)
            if (!_actions.TryAdd(action.Action, action))
                throw new InvalidOperationException($"Duplicate plan action: {action.Action}");
    }

    public static PlanActions CreateDefault() => new(
    [
        new SetParameterAction(),
        new CreateLevelAction(),
        new CreateGridAction(),
        new MoveCadAction(),
    ]);

    public bool TryGet(string action, out IPlanAction handler) => _actions.TryGetValue(action, out handler!);
}
