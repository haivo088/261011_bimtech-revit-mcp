using Bimtech.RevitMcp.Commands.Common;

namespace Bimtech.RevitMcp.Commands.Elements;

// Lists elements matching ElementQuery params, one page at a time.
public sealed class GetElementsCommand : IRevitCommand
{
    public string Method => "get_elements";

    public object? Execute(CommandContext context)
    {
        var document = context.RequireDocument();
        var query = ElementQuery.From(context.Params);
        var page = PageRequest.From(context.Params);
        var info = new ElementInfo(document);
        return Paging.ToPage(query.Apply(document), page, info.Summary, context.Cancellation);
    }
}
