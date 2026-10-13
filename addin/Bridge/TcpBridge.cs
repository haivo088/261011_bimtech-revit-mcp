using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Bimtech.RevitMcp.Commands;
using Bimtech.RevitMcp.Protocol;

namespace Bimtech.RevitMcp.Bridge;

// This class transports JSON. Revit API calls belong in RevitRequestHandler.
public sealed class TcpBridge : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly RevitRequestHandler _handler;
    private bool _disposed;

    // Reading and writing are fast; only execution time depends on the request.
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);
    internal const int DefaultTimeoutMs = 15000;
    internal const int MinTimeoutMs = 1000;
    internal const int MaxTimeoutMs = 120000;
    private const int MaxRequestChars = 256 * 1024;

    public TcpBridge(int port, RevitRequestHandler handler)
    {
        _handler = handler;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        _ = AcceptClientsAsync();
    }

    private async Task AcceptClientsAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_lifetime.Token);
                _ = HandleClientAsync(client);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Trace.WriteLine($"BIMTECH MCP listener: {exception.Message}"); }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true)
                    { AutoFlush = true, NewLine = "\n" };

                string? line;
                using (var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
                {
                    readTimeout.CancelAfter(ReadTimeout);
                    line = await ReadRequestLineAsync(reader, readTimeout.Token);
                }
                if (line is null) return;

                var response = await ProcessRequestAsync(line);
                using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                writeTimeout.CancelAfter(WriteTimeout);
                await writer.WriteLineAsync(response.AsMemory(), writeTimeout.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { Trace.WriteLine($"BIMTECH MCP client: {exception.Message}"); }
        }
    }

    private static async Task<string?> ReadRequestLineAsync(StreamReader reader, CancellationToken cancellation)
    {
        var message = new StringBuilder();
        var buffer = new char[1024];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellation);
            if (count == 0) return null;
            var newline = Array.IndexOf(buffer, '\n', 0, count);
            message.Append(buffer, 0, newline < 0 ? count : newline);
            if (message.Length > MaxRequestChars) throw new IOException("Request exceeds the 256 KiB character limit.");
            if (newline >= 0) return message.ToString();
        }
    }

    // Always returns a JSON-RPC response, including when the request times out.
    private async Task<string> ProcessRequestAsync(string line)
    {
        string? id = null;
        var timeoutMs = DefaultTimeoutMs;
        try
        {
            var request = JsonSerializer.Deserialize<RpcRequest>(line, JsonOptions);
            id = request?.Id;
            if (request?.Jsonrpc != "2.0" || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(request.Method))
                throw new RpcException(RpcErrorCodes.InvalidRequest, "Expected JSON-RPC 2.0 with a string ID and a method.");

            timeoutMs = Math.Clamp(request.TimeoutMs ?? DefaultTimeoutMs, MinTimeoutMs, MaxTimeoutMs);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(timeoutMs);
            var result = await _handler.ExecuteAsync(request, timeout.Token);
            return JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }, JsonOptions);
        }
        catch (JsonException exception) { return Error(id, RpcErrorCodes.ParseError, exception.Message); }
        catch (RpcException exception) { return Error(id, exception.Code, exception.Message, exception.ErrorData); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return Error(id, RpcErrorCodes.BridgeStopped, "The BIMTECH bridge stopped before the request finished.");
        }
        catch (OperationCanceledException)
        {
            return Error(id, RpcErrorCodes.Timeout,
                $"Revit did not finish within {timeoutMs} ms. Revit may be busy or showing a dialog; "
                + "if the command was already running it may still complete.",
                new { timeoutMs });
        }
        catch (Exception exception) { return Error(id, RpcErrorCodes.InternalError, exception.Message); }
    }

    private static string Error(string? id, int code, string message, object? data = null) =>
        JsonSerializer.Serialize(data is null
            ? new { jsonrpc = "2.0", id, error = (object)new { code, message } }
            : new { jsonrpc = "2.0", id, error = (object)new { code, message, data } }, JsonOptions);

    // Called from the Revit ribbon command or OnShutdown, on Revit's API thread.
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _listener.Stop();
        _handler.Dispose();
    }
}
