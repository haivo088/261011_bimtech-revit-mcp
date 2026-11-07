using System.IO;
using Bimtech.RevitMcp.Data;
using Bimtech.RevitMcp.Protocol;

namespace Bimtech.RevitMcp.Commands.Structural;

// Reading a clean data file (grid/1.0, column/1.0, ...) named by a command parameter: .json only, must exist,
// bounded size; schema errors become InvalidParams on that parameter.
internal static class DataFileInput
{
    private const long MaxFileBytes = 5_000_000;

    public static T Read<T>(string path, Func<string, T> parse, string param = "dataPath")
    {
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw RpcException.InvalidParams(param, "expected a .json data file.");
        if (!File.Exists(path))
            throw RpcException.InvalidParams(param, $"file not found: {path}");
        if (new FileInfo(path).Length > MaxFileBytes)
            throw RpcException.InvalidParams(param, $"file is larger than {MaxFileBytes} bytes.");
        try
        {
            return parse(File.ReadAllText(path));
        }
        catch (DataFileException exception)
        {
            throw RpcException.InvalidParams(param, exception.Message);
        }
    }
}
