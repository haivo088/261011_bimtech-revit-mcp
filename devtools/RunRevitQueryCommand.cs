using System.Diagnostics;
using System.Reflection;
using System.IO;
using System.Text;
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
// access. Revit offers no way to abort a running snippet, so the snippet stops itself: QueryGuardInserter adds
// time checks to every loop and a depth limit to every function (timeoutSeconds, QueryGuard.MaxDepth).
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
        // Below the server's 120 s request timeout, so the answer still reaches Claude.
        var timeoutSeconds = context.Params.GetInt("timeoutSeconds", 30, 1, 110);
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
        object? result = null;
        var truncated = false;
        string? error = null;
        var stage = "done";
        using var guard = new FailureGuard(context.UIApplication.Application);
        using (var group = new TransactionGroup(document, "BIMTECH dev query (rolled back)"))
        {
            group.Start();
            QueryGuard.Begin(TimeSpan.FromSeconds(timeoutSeconds), context.Cancellation);
            try
            {
                // Serialized inside the guard and the group: a lazy LINQ result runs the snippet's code only now,
                // and Revit elements are still in their changed state.
                (result, truncated) = Serialize(compiled.Run(query), maxResultChars);
            }
            catch (Exception exception)
            {
                var inner = exception is TargetInvocationException { InnerException: { } cause } ? cause : exception;
                (stage, error) = inner switch
                {
                    QueryTimeoutException => ("timeout", inner.Message),
                    QueryDepthException => ("recursion", inner.Message),
                    OperationCanceledException => ("cancelled", "The client stopped waiting; the query was stopped."),
                    _ => ("run", Truncate(inner.ToString(), MaxErrorChars)),
                };
            }
            finally
            {
                QueryGuard.End();
                if (group.HasStarted() && !group.HasEnded()) group.RollBack();
            }
        }

        return new
        {
            ok = error is null,
            stage,
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
    // Writing stops as soon as the JSON passes the limit, so a huge or endless sequence cannot run on.
    // Timeouts and cancellation pass through; any other serialization failure falls back to ToString().
    private static (object? Value, bool Truncated) Serialize(object? value, int maxChars)
    {
        if (value is null) return (null, false);
        var buffer = new CappedStream(maxChars * 3L);
        try
        {
            JsonSerializer.Serialize(buffer, value, ResultJson);
        }
        catch (CappedStream.FullException)
        {
            return (Truncate(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length), maxChars), true);
        }
        catch (Exception exception) when (exception is not (QueryTimeoutException or QueryDepthException or OperationCanceledException))
        {
            var text = value.ToString() ?? "";
            return (Truncate(text, maxChars), text.Length > maxChars);
        }
        var json = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        return json.Length > maxChars
            ? (Truncate(json, maxChars), true)
            : (JsonSerializer.Deserialize<JsonElement>(json), false);
    }

    private sealed class CappedStream(long capacity) : MemoryStream
    {
        public sealed class FullException : Exception;

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Length + count > capacity) throw new FullException();
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Length + buffer.Length > capacity) throw new FullException();
            base.Write(buffer);
        }
    }

    private static string Truncate(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars] + "…";
}
