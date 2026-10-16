using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bimtech.RevitMcp.DevTools;

// Runs a compiled snippet under QueryGuard (loops stop after the timeout, recursion after QueryGuard.MaxDepth) and
// turns its value into JSON, for send_code (read and preview) and the run_code plan action (the approved run).
// The caller owns the transactions.
internal static class CodeRunner
{
    private const int MaxErrorChars = 4000;

    private static readonly JsonSerializerOptions ResultJson = new()
    {
        MaxDepth = 16,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    // Stage: "done", "timeout", "recursion", "cancelled" or "run" (an exception, in Error).
    internal sealed record Outcome(string Stage, object? Result, bool Truncated, string? Error)
    {
        public bool Ok => Error is null;
    }

    public static Outcome Run(Func<QueryContext, object?> run, QueryContext context, TimeSpan timeout, int maxResultChars)
    {
        QueryGuard.Begin(timeout, context.Cancellation);
        try
        {
            // Serialized inside the guard: a lazy LINQ result runs the snippet's code only now, while Revit elements
            // are still in their changed state.
            var (result, truncated) = Serialize(run(context), maxResultChars);
            return new Outcome("done", result, truncated, null);
        }
        catch (Exception exception)
        {
            var inner = exception is TargetInvocationException { InnerException: { } cause } ? cause : exception;
            var (stage, error) = inner switch
            {
                QueryTimeoutException => ("timeout", inner.Message),
                QueryDepthException => ("recursion", inner.Message),
                OperationCanceledException => ("cancelled", "The client stopped waiting; the code was stopped."),
                _ => ("run", Truncate(inner.ToString(), MaxErrorChars)),
            };
            return new Outcome(stage, null, false, error);
        }
        finally
        {
            QueryGuard.End();
        }
    }

    // The identity of a snippet: commit_plan runs only the exact code that was previewed and approved.
    public static string Hash(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))).ToLowerInvariant();

    public static string Truncate(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars] + "…";

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
}
