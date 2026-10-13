namespace Bimtech.RevitMcp.Commands.Documents;

public sealed class GetDocumentInfoCommand : IRevitCommand
{
    public string Method => "get_document_info";

    public object? Execute(CommandContext context)
    {
        var document = context.RequireDocument();
        // ActiveView can be null, e.g. while Revit is switching documents.
        var view = document.ActiveView;
        return new
        {
            title = document.Title,
            path = document.PathName,
            isFamilyDocument = document.IsFamilyDocument,
            isWorkshared = document.IsWorkshared,
            isReadOnly = document.IsReadOnly,
            activeView = view is null ? null : new
            {
                id = RevitJson.Id(view.Id),
                name = view.Name,
                type = view.ViewType.ToString(),
            },
        };
    }
}
