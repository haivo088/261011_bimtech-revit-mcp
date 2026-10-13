using Bimtech.RevitMcp.Commands.Common;

namespace Bimtech.RevitMcp.Commands.Elements;

public sealed class GetSelectedElementsCommand : IRevitCommand
{
    public string Method => "get_selected_elements";
    public CommandAccess Access => CommandAccess.Read;

    public object? Execute(CommandContext context)
    {
        var uiDocument = context.RequireUIDocument();
        var document = uiDocument.Document;
        var page = PageRequest.From(context.Params);
        var info = new ElementInfo(document);
        var selected = uiDocument.Selection.GetElementIds()
            .OrderBy(id => id.Value)
            .Select(document.GetElement)
            .Where(element => element is not null);
        return Paging.ToPage(selected, page, info.Summary, context.Cancellation);
    }
}
