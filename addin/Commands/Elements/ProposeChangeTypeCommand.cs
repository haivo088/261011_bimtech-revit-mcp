using System.Text.Json;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Commands.Plans;
using Bimtech.RevitMcp.Planning;
using Bimtech.RevitMcp.Protocol;
using Bimtech.RevitMcp.Ui;

namespace Bimtech.RevitMcp.Commands.Elements;

// Proposes changing existing elements to another type of the same category, e.g. walls written with the wrong type.
// One review item per current type → target type, with counts (TypeChangeGroups). Elements already of the target
// type are skipped; elements of another category, or that Revit does not allow on that type, are refused and listed.
// Read-only: it saves a plan but never opens a Transaction; change_type sets the types at commit.
public sealed class ProposeChangeTypeCommand(PlanService plans, ReviewWindowHost review, ModelChangeTracker changes) : IRevitCommand
{
    public string Method => "propose_change_type";
    // Saves a plan file but never changes the model.
    public CommandAccess Access => CommandAccess.Read;

    private const int MaxElements = 1000;
    private const int MaxListed = 50;

    public object? Execute(CommandContext context)
    {
        var p = context.Params;
        var document = context.RequireDocument();
        if (document.IsFamilyDocument)
            throw new RpcException(RpcErrorCodes.NotWritable, "The active document is a family. Open a project model.");
        var ids = p.GetElementIds("elementIds", MaxElements).Distinct().ToList();
        if (ids.Count == 0)
            throw RpcException.InvalidParams("elementIds", "pass at least one element ID.");
        var target = RequireTargetType(document, p.RequireElementId("typeId"));

        var sorted = TypeChangeGroups.Sort(ids.Select(id => Describe(document, id, target)), RevitJson.Id(target.Id),
            target.Category is null ? null : RevitJson.Id(target.Category.Id), target.Category?.Name);
        var refused = sorted.Refused.Take(MaxListed).Select(r => new { elementId = r.ElementId, reason = r.Reason }).ToList();
        if (sorted.Groups.Count == 0)
        {
            if (sorted.Refused.Count > 0 && sorted.AlreadyOfType.Count == 0)
                throw RpcException.InvalidParams("elementIds",
                    $"no element can take type '{target.Name}': " + string.Join(" ", sorted.Refused.Select(r => r.Reason).Distinct()));
            return new
            {
                plan = (object?)null,
                alreadyOfType = sorted.AlreadyOfType.Count,
                refused,
                note = $"Every element is already of type '{target.Name}'. No plan was created.",
            };
        }

        var items = sorted.Groups.Select(group => ToItem(document, group, target)).ToList();
        var count = sorted.Groups.Sum(group => group.ElementIds.Count);
        var plan = plans.Create(
            PlanStorageLocation.For(document),
            kind: "change_type",
            title: $"Đổi {count} phần tử sang Type {target.Name}",
            scope: JsonSerializer.SerializeToElement(new
            {
                type = new { id = RevitJson.Id(target.Id), name = target.Name, category = target.Category?.Name },
                elementCount = ids.Count,
                alreadyOfType = sorted.AlreadyOfType.Count,
                refused = sorted.Refused.Count,
            }),
            items: items,
            modelStamp: changes.Current(document));
        review.Show(plan, context.UIApplication);

        return new
        {
            plan = PlanResults.Status(plan),
            elementsToChange = count,
            alreadyOfType = sorted.AlreadyOfType.Count,
            refusedCount = sorted.Refused.Count,
            refused,
            reviewWindow = "opened",
            note = "Nothing was changed in the model. Each item is one current type → the new type. "
                + (sorted.Refused.Count > 0 ? "Tell the user which elements were refused and why. " : "")
                + "Ask the user to review in Revit and say when they are done, then call get_plan_status and commit_plan.",
        };
    }

    // Walls need a WallType, family instances a FamilySymbol of their category; any other element type of a category
    // (floors, roofs...) works the same way through Element.GetValidTypes.
    private static ElementType RequireTargetType(Document document, ElementId id) =>
        document.GetElement(id) is ElementType { Category: not null } type
            ? type
            : throw RpcException.InvalidParams("typeId",
                "is not an element type with a category. Use get_elements with includeTypes=true to find type IDs.");

    private static TypeChangeGroups.Element Describe(Document document, ElementId id, ElementType target)
    {
        var element = document.GetElement(id);
        if (element is null or ElementType)
            return new(RevitJson.Id(id), null, null, null, null, false);
        var typeId = element.GetTypeId();
        var type = typeId == ElementId.InvalidElementId ? null : document.GetElement(typeId);
        return new(RevitJson.Id(id),
            element.Category is null ? null : RevitJson.Id(element.Category.Id), element.Category?.Name,
            type is null ? null : RevitJson.Id(typeId), type?.Name,
            type is not null && element.GetValidTypes().Contains(target.Id));
    }

    private static PlanItem ToItem(Document document, TypeChangeGroups.Group group, ElementType target)
    {
        var reasons = new List<string>();
        // A wall takes the thickness of its type: say so when it changes.
        if (document.GetElement(new ElementId(long.Parse(group.TypeId))) is WallType from && target is WallType to
            && Math.Abs(from.Width - to.Width) > UnitUtils.ConvertToInternalUnits(0.5, UnitTypeId.Millimeters))
            reasons.Add($"Bề dày đổi {Mm(from.Width):0} → {Mm(to.Width):0} mm.");
        var count = group.ElementIds.Count;
        return new PlanItem
        {
            ItemId = "T-" + group.TypeId,
            Action = "change_type",
            Params = JsonSerializer.SerializeToElement(new
            {
                summary = $"Đổi Type {count} phần tử: {group.TypeName} → {target.Name}",
                fromTypeId = group.TypeId,
                fromTypeName = group.TypeName,
                typeId = RevitJson.Id(target.Id),
                typeName = target.Name,
                elementIds = group.ElementIds,
            }),
            Tier = PlanTier.Review,
            Reasons = reasons,
            Source = JsonSerializer.SerializeToElement(new
            {
                name = TypeChangeGroups.Label(group.TypeName, target.Name, count),
                category = group.CategoryName,
                count,
            }),
        };
    }

    private static double Mm(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);
}
