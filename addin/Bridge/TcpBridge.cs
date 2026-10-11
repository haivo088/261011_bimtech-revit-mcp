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
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true)
                    { AutoFlush = true, NewLine = "\n" };
                var line = await ReadRequestLineAsync(reader, timeout.Token);
                if (line is null) return;
                var response = await ProcessRequestAsync(line, timeout.Token);
                await writer.WriteLineAsync(response.AsMemory(), timeout.Token);
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
            if (message.Length > 65536) throw new IOException("Request exceeds the 64 KiB character limit.");
            if (newline >= 0) return message.ToString();
        }
    }

    private async Task<string> ProcessRequestAsync(string line, CancellationToken cancellation)
    {
        string? id = null;
        try
        {
            var request = JsonSerializer.Deserialize<RpcRequest>(line, JsonOptions);
            id = request?.Id;
            if (request?.Jsonrpc != "2.0" || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(request.Method))
                throw new RpcException(-32600, "Expected JSON-RPC 2.0 with a string ID and a method.");
            var result = await _handler.ExecuteAsync(request, cancellation);
            return JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }, JsonOptions);
        }
        catch (JsonException exception) { return Error(id, -32700, exception.Message); }
        catch (RpcException exception) { return Error(id, exception.Code, exception.Message); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { return Error(id, -32603, exception.Message); }
    }

    private static string Error(string? id, int code, string message) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code, message } }, JsonOptions);

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
