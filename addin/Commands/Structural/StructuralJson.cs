using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands.Common;
using Bimtech.RevitMcp.Protocol;
using Bimtech.StructuralCore.Cad;
using Bimtech.StructuralCore.Columns;

namespace Bimtech.RevitMcp.Commands.Structural;

// Shared lookups and JSON shapes for the CAD-to-structure commands.
internal static class StructuralJson
{
    public static object Ref(Element element) => new { id = RevitJson.Id(element.Id), name = ElementQuery.SafeName(element) };

    // A CAD instance is named after its type (the file or block name); the instance name is just "location".
    public static object CadRef(Document document, ImportInstance cad)
    {
        var type = document.GetElement(cad.GetTypeId());
        return new { id = RevitJson.Id(cad.Id), name = ElementQuery.SafeName(type ?? cad) };
    }

    public static object ColumnType(FamilySymbol symbol) => new
    {
        id = RevitJson.Id(symbol.Id),
        name = symbol.Name,
        family = symbol.FamilyName,
        familyId = RevitJson.Id(symbol.Family.Id),
        bMm = Round(ColumnTypeMatcher.ReadLengthMm(symbol, ColumnTypeMatcher.ColumnBParam)),
        hMm = Round(ColumnTypeMatcher.ReadLengthMm(symbol, ColumnTypeMatcher.ColumnHParam)),
    };

    public static ImportInstance RequireCad(Document document, ElementId id, string param) =>
        document.GetElement(id) as ImportInstance
        ?? throw RpcException.InvalidParams(param, "is not a CAD import or link. Use list_cad_sources to find CAD IDs.");

    public static Level RequireLevel(Document document, ElementId id, string param) =>
        document.GetElement(id) as Level
        ?? throw RpcException.InvalidParams(param, "is not a level. Use get_levels to find level IDs.");

    // viewId; when omitted, the view a view-specific CAD belongs to, else the active view.
    // CAD geometry is read as shown in this plan view.
    public static ViewPlan RequirePlanView(CommandContext context, Document document, ImportInstance cad)
    {
        var view = context.Params.Has("viewId")
            ? document.GetElement(context.Params.RequireElementId("viewId")) as View
            : cad.ViewSpecific ? document.GetElement(cad.OwnerViewId) as View : document.ActiveView;
        return view as ViewPlan
            ?? throw RpcException.InvalidParams("viewId",
                "must be a plan view that shows the CAD. Open a floor or structural plan, or pass viewId.");
    }

    // Optional {minX, minY, maxX, maxY} in millimeters, model coordinates.
    public static CadGeometry.PlanRegion? ReadRegion(CommandParams? region)
    {
        if (region is null) return null;
        double Feet(string name) => UnitUtils.ConvertToInternalUnits(region.RequireDouble(name), UnitTypeId.Millimeters);
        var (minX, minY, maxX, maxY) = (Feet("minX"), Feet("minY"), Feet("maxX"), Feet("maxY"));
        if (minX >= maxX || minY >= maxY)
            throw RpcException.InvalidParams("region", "minX/minY must be smaller than maxX/maxY.");
        return new CadGeometry.PlanRegion(minX, maxX, minY, maxY);
    }

    private static double? Round(double? value) => value is null ? null : Math.Round(value.Value, 1);
}
