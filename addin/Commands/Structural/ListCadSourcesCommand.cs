using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands.Common;

namespace Bimtech.RevitMcp.Commands.Structural;

// CAD imports and links in the model: the starting point for every scan_cad_* tool.
public sealed class ListCadSourcesCommand : IRevitCommand
{
    public string Method => "list_cad_sources";

    public object? Execute(CommandContext context)
    {
        var document = context.RequireDocument();
        var activeView = document.ActiveView;
        var sources = new FilteredElementCollector(document)
            .OfClass(typeof(ImportInstance))
            .Cast<ImportInstance>()
            .OrderBy(cad => cad.Id.Value)
            .Select(cad =>
            {
                var ownerView = cad.ViewSpecific ? document.GetElement(cad.OwnerViewId) : null;
                return new
                {
                    cad = StructuralJson.CadRef(document, cad),
                    isLinked = cad.IsLinked,
                    viewSpecific = cad.ViewSpecific,
                    ownerView = ownerView is null ? null : StructuralJson.Ref(ownerView),
                };
            })
            .ToList();

        return new
        {
            activeView = activeView is null ? null : new
            {
                id = RevitJson.Id(activeView.Id),
                name = activeView.Name,
                isPlan = activeView is ViewPlan,
            },
            sources,
        };
    }
}
