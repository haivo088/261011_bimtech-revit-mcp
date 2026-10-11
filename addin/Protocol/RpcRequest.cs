using System.Text.Json;

namespace Bimtech.RevitMcp.Protocol;

// Both sides send one JSON-RPC object followed by a newline.
public sealed class RpcRequest
{
    public string? Jsonrpc { get; init; }
    public string? Id { get; init; }
    public string? Method { get; init; }
    public JsonElement Params { get; init; }
}

public sealed class RpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}
