using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Cad;
using Bimtech.RevitMcp.Commands.Common;
using Bimtech.RevitMcp.Commands.Plans;
using Bimtech.RevitMcp.Planning;
using Bimtech.RevitMcp.Protocol;
using Bimtech.RevitMcp.Recognition;
using Bimtech.RevitMcp.Ui;

namespace Bimtech.RevitMcp.Commands.Structural;

// Step 2 of setting up a structural model, after propose_levels is committed. Reads the grids of one CAD with
// GridRecognizer (line pattern first, then grid bubbles, then layer name; or the layer the person names), snaps each
// bay to a module (GridLayout.Snap: 5998.7 → 6000) and places the grids so the first vertical and first horizontal
// axis cross at the Project Base Point. Every CAD is moved by its own vector: the axes of each storey's drawing are
// matched to the source grid (GridLayout.Match), since storeys are often drawn at different places.
// Read-only: it saves a plan but never opens a Transaction.
public sealed class ProposeGridsCommand(PlanService plans, ReviewWindowHost review, ModelChangeTracker changes) : IRevitCommand
{
    public string Method => "propose_grids";
    // Saves a plan file but never changes the model.
    public CommandAccess Access => CommandAccess.Read;

    private const int MaxAlignedCad = 200;
    private const int MaxEntities = 300_000;
    // Below this the CAD already sits where it should; no move item is proposed.
    private const double MinMoveMm = 0.5;

    private sealed record Axes(List<RecognizedGrid> Vertical, List<RecognizedGrid> Horizontal)
    {
        public List<double> Xs => Vertical.Select(grid => grid.X0).ToList();
        public List<double> Ys => Horizontal.Select(grid => grid.Y0).ToList();
    }

    public object? Execute(CommandContext context)
    {
        var p = context.Params;
        var document = context.RequireDocument();
        var cad = StructuralJson.RequireCad(document, p.RequireElementId("cadId"), "cadId");
        var view = StructuralJson.RequirePlanView(context, document, cad);
        var aligned = p.GetElementIds("alignCadIds", MaxAlignedCad)
            .Select((id, index) => StructuralJson.RequireCad(document, id, $"alignCadIds[{index}]"))
            .Where(other => other.Id != cad.Id)
            .DistinctBy(other => other.Id)
            .ToList();
        var gridLayer = p.GetString("gridLayer", 255);
        var module = p.GetDouble("snapModuleMm", GridLayout.DefaultModuleMm, 0, 1000);
        var tolerance = p.GetDouble("snapToleranceMm", GridLayout.DefaultToleranceMm, 0, 100);
        var pin = p.GetBool("pin", true);

        var recognition = GridRecognizer.Recognize(CadExtractor.Extract(document, cad, view, new(MaxEntities)), gridLayer);
        var source = Split(recognition);
        if (source.Vertical.Count == 0 || source.Horizontal.Count == 0)
        {
            var param = gridLayer is null ? "cadId" : "gridLayer";
            // Like RpcException.InvalidParams, plus the style scores so Claude can suggest the grid layer.
            throw new RpcException(RpcErrorCodes.InvalidParams,
                $"Parameter '{param}': no grid found in both directions. " + string.Join(" ", recognition.Warnings)
                + " Pass gridLayer with the CAD layer of the grid lines.",
                new { param, styleScores = recognition.Styles.Take(10) });
        }
        context.Cancellation.ThrowIfCancellationRequested();

        var basePoint = BasePoint.GetProjectBasePoint(document).Position;
        var (baseX, baseY) = (UnitConvert.Length(basePoint.X), UnitConvert.Length(basePoint.Y));
        var xs = GridLayout.Snap(source.Xs, module, tolerance);
        var ys = GridLayout.Snap(source.Ys, module, tolerance);
        var shift = (X: baseX - source.Xs[0], Y: baseY - source.Ys[0]);

        var items = new List<PlanItem>();
        var moves = new List<(ImportInstance Cad, double Dx, double Dy, string Reason)>
        {
            (cad, shift.X, shift.Y, "CAD gốc: trục đầu tiên mỗi phương về Project Base Point."),
        };
        foreach (var other in aligned)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            moves.Add(AlignOther(context, document, other, view, gridLayer, source, xs, ys, (baseX, baseY), shift));
        }
        items.AddRange(moves
            .Where(move => Math.Sqrt(move.Dx * move.Dx + move.Dy * move.Dy) >= MinMoveMm)
            .Select((move, index) => MoveItem(document, move.Cad, index, move.Dx, move.Dy, move.Reason, pin)));

        // Grid lines span the recognized extent of the other direction, moved with the CAD.
        var (yMin, yMax) = (source.Vertical.Min(grid => Math.Min(grid.Y0, grid.Y1)) + shift.Y, source.Vertical.Max(grid => Math.Max(grid.Y0, grid.Y1)) + shift.Y);
        var (xMin, xMax) = (source.Horizontal.Min(grid => Math.Min(grid.X0, grid.X1)) + shift.X, source.Horizontal.Max(grid => Math.Max(grid.X0, grid.X1)) + shift.X);
        var existing = new FilteredElementCollector(document).OfClass(typeof(Grid)).Cast<Grid>()
            .Select(grid => grid.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        items.AddRange(source.Vertical.Select((grid, i) =>
            GridItem(grid, baseX + xs[i].OffsetMm, yMin, baseX + xs[i].OffsetMm, yMax, xs[i], existing, planned, pin)));
        items.AddRange(source.Horizontal.Select((grid, i) =>
            GridItem(grid, xMin, baseY + ys[i].OffsetMm, xMax, baseY + ys[i].OffsetMm, ys[i], existing, planned, pin)));

        var plan = plans.Create(
            PlanStorageLocation.For(document),
            kind: "create_grids",
            title: $"Dựng {source.Vertical.Count + source.Horizontal.Count} trục từ CAD {StructuralJson.CadName(document, cad)}",
            scope: JsonSerializer.SerializeToElement(new
            {
                cad = StructuralJson.CadRef(document, cad),
                view = StructuralJson.Ref(view),
                gridStyles = recognition.ChosenStyles,
                snapModuleMm = module,
                snapToleranceMm = tolerance,
                warnings = recognition.Warnings,
                alignedCad = aligned.Select(other => StructuralJson.CadRef(document, other)),
            }),
            items: items,
            modelStamp: changes.Current(document));
        review.Show(plan, context.UIApplication);

        return new
        {
            plan = PlanResults.Status(plan),
            grids = new
            {
                vertical = source.Vertical.Select((grid, i) => new { grid.Name, grid.NameSource, offsetMm = xs[i].OffsetMm }),
                horizontal = source.Horizontal.Select((grid, i) => new { grid.Name, grid.NameSource, offsetMm = ys[i].OffsetMm }),
            },
            snappedBays = xs.Concat(ys).Count(axis => axis.Note is not null),
            warnings = recognition.Warnings,
            styleScores = recognition.Styles.Take(5),
            existingGridNames = items.Where(item => item.Tier == PlanTier.Blocked).Select(item => item.ItemId),
            reviewWindow = "opened",
            note = "Nothing was changed in the model. Grid 1-A (the first vertical and horizontal axis) sits on the Project "
                + "Base Point; bays are snapped to the module and each item says how. Each CAD is moved by its own vector "
                + "where its grid matched; check the move items that say they used the source vector. Ask the user to "
                + "review in Revit and say when they are done, then call get_plan_status and commit_plan.",
        };
    }

    private static Axes Split(GridRecognition recognition) => new(
        recognition.Grids.Where(grid => grid.Vertical).OrderBy(grid => grid.X0).ToList(),
        recognition.Grids.Where(grid => !grid.Vertical).OrderBy(grid => grid.Y0).ToList());

    // Moves another storey's CAD by its own vector when its axes match the source grid in both directions, else by
    // the source vector, saying why, for the reviewer to check.
    private static (ImportInstance, double, double, string) AlignOther(
        CommandContext context, Document document, ImportInstance other, View sourceView, string? gridLayer, Axes source,
        IReadOnlyList<GridLayout.Placed> xs, IReadOnlyList<GridLayout.Placed> ys, (double X, double Y) basePoint,
        (double X, double Y) sourceShift)
    {
        var view = (other.ViewSpecific ? document.GetElement(other.OwnerViewId) as View : null) ?? sourceView;
        var axes = Split(GridRecognizer.Recognize(CadExtractor.Extract(document, other, view, new(MaxEntities)), gridLayer));
        var column = GridLayout.Match(source.Xs, axes.Xs);
        var row = GridLayout.Match(source.Ys, axes.Ys);
        if (column is { } i && row is { } j)
            return (other, basePoint.X + xs[i].OffsetMm - axes.Xs[0], basePoint.Y + ys[j].OffsetMm - axes.Ys[0],
                $"Căn theo trục của chính CAD này ({axes.Xs.Count} trục đứng, {axes.Ys.Count} trục ngang khớp với CAD gốc).");

        var why = axes.Vertical.Count == 0 || axes.Horizontal.Count == 0
            ? "không tìm thấy trục trong CAD này"
            : "hệ trục không khớp duy nhất với CAD gốc";
        return (other, sourceShift.X, sourceShift.Y,
            $"Dời theo vector của CAD gốc vì {why}: chỉ đúng khi hai bản vẽ cùng hệ toạ độ. Kiểm tra CAD này nằm đúng trục sau khi ghi.");
    }

    private static PlanItem MoveItem(Document document, ImportInstance cad, int index, double dxMm, double dyMm, string reason, bool pin) => new()
    {
        ItemId = "M" + (index + 1).ToString(CultureInfo.InvariantCulture),
        Action = "move_cad",
        Params = JsonSerializer.SerializeToElement(new
        {
            summary = $"Dời CAD {StructuralJson.CadName(document, cad)} {Mm(dxMm)}, {Mm(dyMm)} mm",
            cadId = RevitJson.Id(cad.Id),
            dxMm = Math.Round(dxMm, 2),
            dyMm = Math.Round(dyMm, 2),
            pin,
        }),
        Reasons = cad.Pinned ? [reason, "CAD đang ghim: bỏ ghim để dời rồi ghim lại."] : [reason],
        Source = JsonSerializer.SerializeToElement(new
        {
            elementId = RevitJson.Id(cad.Id),
            name = StructuralJson.CadName(document, cad),
            category = "CAD",
        }),
    };

    // planned: names given to earlier items; a second grid with the same bubble name is blocked and gets its own item ID.
    private static PlanItem GridItem(
        RecognizedGrid grid, double x0, double y0, double x1, double y1, GridLayout.Placed placed, HashSet<string> existing,
        HashSet<string> planned, bool pin)
    {
        var duplicate = !planned.Add(grid.Name);
        var nameTaken = existing.Contains(grid.Name) || duplicate;
        var reasons = new List<string>();
        if (existing.Contains(grid.Name)) reasons.Add($"Model đã có Grid tên '{grid.Name}'.");
        if (duplicate) reasons.Add($"Bản vẽ có hơn một trục tên '{grid.Name}': dựng tay trục này với tên đúng.");
        if (placed.Note is not null) reasons.Add(placed.Note);
        reasons.AddRange(grid.Reasons);
        var position = grid.Vertical ? $"x = {Mm(placed.OffsetMm)}" : $"y = {Mm(placed.OffsetMm)}";
        var bay = placed.SpacingMm > 0 ? $", nhịp {Mm(placed.SpacingMm)}" : "";
        return new PlanItem
        {
            ItemId = "G-" + grid.Name + (duplicate ? $"@{Mm(placed.OffsetMm)}" : ""),
            Action = "create_grid",
            Confidence = grid.Confidence,
            Params = JsonSerializer.SerializeToElement(new
            {
                summary = $"Trục {grid.Name} ({(grid.Vertical ? "đứng" : "ngang")}): {position} mm từ gốc{bay}"
                    + (grid.NameSource == "auto" ? " · tên tự đánh" : ""),
                name = grid.Name,
                startMm = new { x = Math.Round(x0, 2), y = Math.Round(y0, 2) },
                endMm = new { x = Math.Round(x1, 2), y = Math.Round(y1, 2) },
                direction = grid.Vertical ? "vertical" : "horizontal",
                measuredSpacingMm = Math.Round(placed.MeasuredSpacingMm, 2),
                pin,
            }),
            Tier = nameTaken ? PlanTier.Blocked : PlanTier.Review,
            Reasons = reasons,
            Source = JsonSerializer.SerializeToElement(new { name = grid.Name, category = "Grids" }),
        };
    }

    private static string Mm(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
