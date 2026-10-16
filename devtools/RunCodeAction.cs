using System.Text.Json;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Planning.Actions;

namespace Bimtech.RevitMcp.DevTools;

// Writes the item of send_code with write=true, after the person approved it:
//   { "code": "...", "codeHash": "<sha256>", "args": { ... } | null, "purpose": "...", "script": "set/name" | null,
//     "timeoutSeconds": 30, "historyFile": "..." | null }
// Runs inside the Transaction RevitPlanWriter opened, so the snippet must not start its own (SubTransaction is fine).
// The code must still match codeHash, so a plan file edited on disk cannot run something nobody reviewed.
public sealed class RunCodeAction : IPlanAction
{
    public const string Name = "run_code";
    public string Action => Name;

    private const int MaxResultChars = 20_000;

    public object Apply(Document document, JsonElement parameters)
    {
        var code = ActionParams.OptionalText(parameters, "code") ?? throw new PlanActionException("The plan item has no code.");
        var hash = CodeRunner.Hash(code);
        if (!string.Equals(hash, ActionParams.OptionalText(parameters, "codeHash"), StringComparison.Ordinal))
            throw new PlanActionException("The code differs from the one that was previewed and approved. Send it again with send_code.");
        var args = parameters.TryGetProperty("args", out var value) && value.ValueKind == JsonValueKind.Object ? value : (JsonElement?)null;
        var timeout = TimeSpan.FromSeconds(ActionParams.OptionalNumber(parameters, "timeoutSeconds") ?? 30);
        var historyFile = ActionParams.OptionalText(parameters, "historyFile");
        void Record(bool ok, string stage) => CodeHistory.Append(historyFile, new CodeHistory.Entry(
            DateTimeOffset.Now, "commit", ActionParams.OptionalText(parameters, "purpose"), ActionParams.OptionalText(parameters, "script"),
            true, ok, stage, hash, null, document.Title));

        var compiled = QueryCompiler.Compile(code);
        if (compiled.Run is null)
        {
            Record(false, "compile");
            throw new PlanActionException("The code no longer compiles: "
                + string.Join(" | ", compiled.Errors.Take(3).Select(error => $"line {error.Line}: {error.Message}")));
        }

        var context = new QueryContext(document, null, null, CancellationToken.None, args);
        var outcome = CodeRunner.Run(compiled.Run, context, timeout, MaxResultChars);
        Record(outcome.Ok, outcome.Stage);
        if (!outcome.Ok)
            throw new PlanActionException($"{outcome.Stage}: {CodeRunner.Truncate(outcome.Error ?? "", 1500)}");
        return new { result = outcome.Result, resultTruncated = outcome.Truncated, log = context.Log };
    }
}
