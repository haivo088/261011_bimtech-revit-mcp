namespace Bimtech.RevitMcp.Protocol;

// Codes -32700..-32600 come from JSON-RPC 2.0. Codes -32001.. are this bridge's own.
public static class RpcErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    public const int NoDocument = -32001;
    public const int EventDenied = -32002;
    public const int Timeout = -32003;
    public const int BridgeStopped = -32004;
    public const int ResponseTooLarge = -32005;
    public const int NotWritable = -32006;
    public const int SessionExpired = -32007;
    public const int CommandDisabled = -32008;
    public const int RevitApiError = -32010;
}

// Thrown by commands to send a specific error back to Claude.
// ErrorData is optional, must serialize to JSON, and should help the caller fix the request.
public sealed class RpcException(int code, string message, object? data = null) : Exception(message)
{
    public int Code { get; } = code;
    public object? ErrorData { get; } = data;

    public static RpcException InvalidParams(string param, string message) =>
        new(RpcErrorCodes.InvalidParams, $"Parameter '{param}': {message}", new { param });
}
