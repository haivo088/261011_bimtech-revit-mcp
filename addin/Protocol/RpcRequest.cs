using System.Text.Json;

namespace Bimtech.RevitMcp.Protocol;

// Both sides send one JSON-RPC object followed by a newline.
public sealed class RpcRequest
{
    public string? Jsonrpc { get; init; }
    public string? Id { get; init; }
    public string? Method { get; init; }
    // Undefined when the sender omits params; CommandParams treats that as {}.
    public JsonElement Params { get; init; }
    // How long this request may take in Revit. Not part of JSON-RPC; the bridge clamps it.
    public int? TimeoutMs { get; init; }
}
