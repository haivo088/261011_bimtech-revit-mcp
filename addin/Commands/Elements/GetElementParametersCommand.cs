using Bimtech.RevitMcp.Commands.Common;
using Bimtech.RevitMcp.Protocol;

namespace Bimtech.RevitMcp.Commands.Elements;

// Reads parameters of a few elements. parameterNames keeps the response small;
// includeType adds the parameters of each element's type.
public sealed class GetElementParametersCommand : IRevitCommand
{
    public const int MaxElements = 50;

    public string Method => "get_element_parameters";

    public object? Execute(CommandContext context)
    {
        var document = context.RequireDocument();
        var ids = context.Params.GetElementIds("elementIds", maxCount: MaxElements);
        if (ids.Count == 0)
            throw RpcException.InvalidParams("elementIds", "must contain at least one element ID.");
        var names = context.Params.GetStringList("parameterNames", maxCount: 100, maxLength: 200);
        var includeType = context.Params.GetBool("includeType", false);

        var info = new ElementInfo(document);
        var elements = new List<object>();
        var notFound = new List<string>();
        foreach (var id in ids)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            var element = document.GetElement(id);
            if (element is null)
            {
                notFound.Add(RevitJson.Id(id));
                continue;
            }
            var type = includeType ? document.GetElement(element.GetTypeId()) : null;
            elements.Add(new
            {
                element = info.Summary(element),
                parameters = ParameterInfo.Select(element, names).Select(p => ParameterInfo.From(p, document)).ToList(),
                typeParameters = type is null
                    ? null
                    : ParameterInfo.Select(type, names).Select(p => ParameterInfo.From(p, document)).ToList(),
            });
        }
        return new { elements, notFound };
    }
}
