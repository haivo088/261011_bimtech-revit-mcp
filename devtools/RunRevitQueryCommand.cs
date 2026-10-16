using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands;
using Bimtech.RevitMcp.Commands.Common;
using Bimtech.RevitMcp.Protocol;

namespace Bimtech.RevitMcp.DevTools;

// Runs a C# snippet against the open model to explore a drawing before a rule becomes a real command.
// Read-only by construction: the snippet runs inside a TransactionGroup that is always rolled back, so any
// transaction it commits is undone too, and FailureGuard keeps Revit dialogs away. It does not stop file or UI
// access, and Revit offers no way to abort a running snippet: loops should check ctx.Cancellation.
public sealed class RunRevitQueryCommand : IRevitCommand
{
    public string Method => "run_revit_query";
    // Every model change is rolled back.
    public CommandAccess Access => CommandAccess.Read;

    private const int MaxErrorChars = 4000;

    private static readonly JsonSerializerOptions ResultJson = new()
    {
        MaxDepth = 16,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public object? Execute(CommandContext context)
    {
        var code = context.Params.RequireString("code", 100_000);
        var maxResultChars = context.Params.GetInt("maxResultChars", 50_000, 1_000, 1_000_000);
        var uiDocument = context.RequireUIDocument();
        var document = uiDocument.Document;
        if (document.IsModifiable)
            throw new RpcException(RpcErrorCodes.NotWritable,
                "Revit is inside another command or transaction. Finish it, then retry.");

        var watch = Stopwatch.StartNew();
        var compiled = QueryCompiler.Compile(code);
        if (compiled.Run is null)
            return new { ok = false, stage = "compile", errors = compiled.Errors, compileMs = watch.ElapsedMilliseconds };
        var compileMs = watch.ElapsedMilliseconds;

        var query = new QueryContext(context.UIApplication, uiDocument, context.Cancellation);
        object? value = null;
        string? error = null;
        using var guard = new FailureGuard(context.UIApplication.Application);
        using (var group = new TransactionGroup(document, "BIMTECH dev query (rolled back)"))
        {
            group.Start();
            try
            {
                value = compiled.Run(query);
            }
            catch (Exception exception)
            {
                var inner = exception is TargetInvocationException { InnerException: { } cause } ? cause : exception;
                error = Truncate(inner.ToString(), MaxErrorChars);
            }
            finally
            {
                if (group.HasStarted() && !group.HasEnded()) group.RollBack();
            }
        }

        var (result, truncated) = Serialize(value, maxResultChars);
        return new
        {
            ok = error is null,
            stage = error is null ? "done" : "run",
            result,
            resultTruncated = truncated,
            log = query.Log,
            error,
            revitErrors = guard.Errors,
            revitWarnings = guard.Warnings,
            compileMs,
            runMs = watch.ElapsedMilliseconds - compileMs,
            note = "Every change the code made to the model was rolled back.",
        };
    }

    // JSON when the value serializes (anonymous objects, lists, numbers...), else its ToString().
    // Revit elements rarely serialize: return the fields you need instead.
    private static (object? Value, bool Truncated) Serialize(object? value, int maxChars)
    {
        if (value is null) return (null, false);
        string text;
        bool isJson;
        try
        {
            text = JsonSerializer.Serialize(value, ResultJson);
            isJson = true;
        }
        catch (Exception)
        {
            text = value.ToString() ?? "";
            isJson = false;
        }
        if (text.Length > maxChars) return (Truncate(text, maxChars), true);
        return (isJson ? JsonSerializer.Deserialize<JsonElement>(text) : text, false);
    }

    private static string Truncate(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars] + "…";
}
