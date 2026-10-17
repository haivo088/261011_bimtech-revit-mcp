using System.Text.Json;
using Autodesk.Revit.DB;
using Bimtech.RevitMcp.Cad;
using Bimtech.RevitMcp.Commands.Common;
using Bimtech.RevitMcp.Commands.Plans;
using Bimtech.RevitMcp.Planning;
using Bimtech.RevitMcp.Protocol;
using Bimtech.RevitMcp.Recognition;
using Bimtech.RevitMcp.Ui;
using Bimtech.StructuralCore.Beams;
using Bimtech.StructuralCore.Cad;
using Bimtech.StructuralCore.Families;
using Bimtech.StructuralCore.Piles;

namespace Bimtech.RevitMcp.Commands.Structural;

// Plans the main beams of one level from a CAD plan, column first (BeamRecognizer): the columns and walls already in
// the model that reach the level are the supports; a beam is a pair of parallel CAD lines leaving a column and reaching
// the next support. Beams run centre to centre of their supports and split only at supports. Read-only: it saves a
// plan but never opens a Transaction. One review item per group (mark and size).
//   - Size: b×h from the mark ("D1 (220x600)"), b measured from the lines when the mark has none; h from the mark,
//     else heightsMm, else the item is Blocked (never guessed).
//   - The beam's offset from the grid is set like columns (GridIntersections): centred or face on the grid within
//     gridToleranceMm set exactly, otherwise rounded to snapMm.
//   - Type per group: "types" entry → existing type with matching b/h → b×h type created in templateFamilyId
//     (default: BTC-DamCN_BeTong when loaded). Beams already in the model at that spot are skipped.
//   - Reported, not placed: edges leaving a column that reach no support (secondary beams, consoles), marks that
//     went to no beam, skewed lines.
//   - secondary=true, after the main beams are committed: the beams in the model at the level become supports and
//     beams start at their side faces (and at walls), so beams resting on beams are found, their ends on the supporting
//     beams' centre lines. Each is weighed on evidence (SecondaryBeams); groups not all sure never auto-approve.
public sealed class ProposeBeamsCommand(PlanService plans, ReviewWindowHost review, ModelChangeTracker changes) : IRevitCommand
{
    public string Method => "propose_beams";
    // Saves a plan file but never changes the model.
    public CommandAccess Access => CommandAccess.Read;

    private const double MarkWidthToleranceMm = 10;
    private const double WellDrawn = 0.9;
    private const int MaxListed = 8;
    private const int MaxReported = 30;
    // Revit lines read to place the DWG file on the CAD.
    private const int AlignmentEntities = 150_000;

    private sealed record Placed(
        BeamRecognizer.Beam Beam, XYZ Start, XYZ End, string At, bool OnGrid, string? Name, double BMm, double? HMm, string? WidthNote);

    public object? Execute(CommandContext context)
    {
        var p = context.Params;
        var document = context.RequireDocument();
        if (document.IsFamilyDocument)
            throw new RpcException(RpcErrorCodes.NotWritable, "The active document is a family. Open a project model.");
        var cad = StructuralJson.RequireCad(document, p.RequireElementId("cadId"), "cadId");
        var view = StructuralJson.RequirePlanView(context, document, cad);
        var level = StructuralJson.RequireLevel(document, p.RequireElementId("levelId"), "levelId");
        var offsetMm = p.GetDouble("offsetMm", 0, -100_000, 100_000);
        var layers = p.GetStringList("layers", maxCount: 50, maxLength: 255);
        var assigned = p.GetStringMap("types", maxCount: 500, maxLength: 32);
        var heights = p.GetNumberMap("heightsMm", 100, 5000, maxCount: 500);
        var templateFamily = p.Has("templateFamilyId")
            ? StructuralJson.RequireFamily(document, p.RequireElementId("templateFamilyId"), BuiltInCategory.OST_StructuralFraming)
            : DefaultFamilies.Find(document, DefaultFamilies.BeamFamilyName);
        var writeMark = p.GetBool("writeMark", true);
        var secondary = p.GetBool("secondary", false);
        var snapMm = p.GetDouble("snapMm", 5, 0, 100);
        var toleranceMm = p.GetDouble("gridToleranceMm", GridIntersections.DefaultToleranceMm, 0, 100);
        var marginMm = p.GetDouble("gridMarginMm", GridIntersections.DefaultMarginMm, 0, 5000);
        var options = new BeamRecognizer.Options
        {
            MinWidthMm = p.GetDouble("minWidthMm", 100, 50, 2000),
            MaxWidthMm = p.GetDouble("maxWidthMm", 1000, 50, 3000),
            ProbeMm = p.GetDouble("probeMm", 300, 50, 3000),
            MaxGapMm = p.GetDouble("maxGapMm", 1500, 0, 10_000),
            MinCoverage = p.GetDouble("minCoverage", 0.6, 0.1, 1),
            TagDistanceMm = p.GetDouble("tagDistanceMm", 1000, 0, 10_000),
            Secondary = secondary,
        };
        if (options.MinWidthMm >= options.MaxWidthMm)
            throw RpcException.InvalidParams("minWidthMm", "must be smaller than maxWidthMm.");

        var frame = PileGridSnapper.PlanGridFrame(document, view);
        var (uAxis, vAxis) = frame is null ? (XYZ.BasisX, XYZ.BasisY) : (frame.UAxis, frame.VAxis);
        double U(XYZ point) => UnitConvert.Length(point.DotProduct(uAxis));
        double V(XYZ point) => UnitConvert.Length(point.DotProduct(vAxis));

        var caseFrame = frame is null ? BeamCase.Frame.Plain : new BeamCase.Frame(uAxis.X, uAxis.Y, frame.AlongU, frame.AlongV);
        // Secondary beams rest on the beams already in the model at this level: main beams are committed first.
        var beamSupports = secondary ? CadExtractor.ModelBeamSupports(document, level) : [];
        if (secondary && beamSupports.Count == 0)
            throw RpcException.InvalidParams("secondary",
                "no beam is in the model at this level. Propose and commit the main beams first (propose_beams without secondary).");
        var supports = BeamCase.Supports(CadExtractor.ModelSupports(document, level.Elevation).Concat(beamSupports), caseFrame);
        if (!supports.Any(support => support.IsColumn))
            throw RpcException.InvalidParams("levelId",
                "no structural column reaches this level. Model the columns below it first (propose_columns).");
        var lines = CadLines.Read(document, cad, view, layers)
            .Select(line => new BeamRecognizer.Line(U(line.A), V(line.A), U(line.B), V(line.B), line.Dashed))
            .ToList();
        if (layers.Count > 0 && lines.Count == 0)
            throw RpcException.InvalidParams("layers", "no line of the CAD is on these layers in this view.");
        var texts = new FilteredElementCollector(document, view.Id).OfClass(typeof(TextNote)).Cast<TextNote>()
            .Where(note => !string.IsNullOrWhiteSpace(note.Text))
            .Select(note => new BeamRecognizer.Text(note.Text, U(note.Coord), V(note.Coord),
                Math.Atan2(note.BaseDirection.DotProduct(vAxis), note.BaseDirection.DotProduct(uAxis))))
            .ToList();
        // Marks are usually texts in the DWG ("D2-X1(350x600)"), not Text Notes: read the file and add its texts.
        object? dwg = null;
        CadCase? data = null;
        if (texts.Count == 0 || p.Has("dwgPath"))
        {
            data = CadExtractor.Extract(document, cad, view, new CadExtractor.Limits(AlignmentEntities));
            var revitTexts = data.Texts.Count;
            dwg = DwgOverlay.Attach(context, document, cad, data, "beam marks are not read, so h is unknown", circles: false);
            texts.AddRange(data.Texts.Skip(revitTexts).Select(text =>
            {
                var radians = text.RotationDeg * Math.PI / 180;
                var direction = new XYZ(Math.Cos(radians), Math.Sin(radians), 0);
                var at = new XYZ(Feet(text.X), Feet(text.Y), 0);
                return new BeamRecognizer.Text(text.Text, U(at), V(at),
                    Math.Atan2(direction.DotProduct(vAxis), direction.DotProduct(uAxis)));
            }));
        }
        context.Cancellation.ThrowIfCancellationRequested();

        var result = BeamRecognizer.Recognize(lines, supports, texts, options);
        context.Cancellation.ThrowIfCancellationRequested();
        // Secondary beams are weighed on evidence; walls and masonry from the DWG's hatches when it was read.
        var wallLoops = data is null ? [] : SecondaryBeams.WallLoops(data.Hatches.SelectMany(ColumnRecognizer.OuterLoops), caseFrame);
        var weights = result.Beams.Where(beam => beam.IsSecondary)
            .ToDictionary(beam => beam, beam => SecondaryBeams.Weigh(beam, result.Beams, caseFrame, wallLoops));

        var z = level.Elevation + Feet(offsetMm);
        XYZ Model(double u, double v) => uAxis * Feet(u) + vAxis * Feet(v) + new XYZ(0, 0, z);
        GridIntersections.AxisFit FitCross(bool alongU, double cross, double half) =>
            GridIntersections.Fit(cross, half, frame is null ? Array.Empty<GridIntersections.Axis>() : alongU ? frame.AlongU : frame.AlongV,
                snapMm, marginMm, toleranceMm);

        var placed = result.Beams.Select(beam =>
        {
            var fit = FitCross(beam.AlongU, beam.Cross, beam.WidthMm / 2);
            var cross = beam.Cross + fit.ShiftMm;
            var (start, end) = beam.AlongU
                ? (Model(beam.Start, cross), Model(beam.End, cross))
                : (Model(cross, beam.Start), Model(cross, beam.End));
            var measured = RoundTo5(beam.WidthMm);
            var widthNote = beam.Tag?.BMm is { } tagged && Math.Abs(tagged - beam.WidthMm) > MarkWidthToleranceMm
                ? $"ký hiệu ghi b={tagged:0}, nét vẽ đo {measured:0}"
                : null;
            var at = $"{beam.From.Label} → {beam.To.Label}" + (fit.OnGrid ? $" (trục {fit.GridName})" : "");
            return new Placed(beam, start, end, at, fit.OnGrid || frame is null, beam.Tag?.Name,
                beam.Tag?.BMm ?? measured, beam.Tag?.HMm, widthNote);
        }).ToList();

        // Group keys like columns: the mark, "D1 220x600" when one mark has several sizes, "220x600", or "b220".
        var sizesPerName = placed.Where(beam => beam.Name is not null).GroupBy(beam => beam.Name!)
            .ToDictionary(group => group.Key, group => group.Select(beam => (beam.BMm, beam.HMm)).Distinct().Count());
        string Key(Placed beam) => beam.Name is { } name
            ? sizesPerName[name] > 1 ? $"{name} {Size(beam.BMm, beam.HMm)}" : name
            : beam.HMm is not null ? Size(beam.BMm, beam.HMm) : $"b{beam.BMm:0}";
        var groups = placed.GroupBy(Key)
            .OrderBy(group => group.First().Name is null ? 1 : 0).ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var keys = groups.Select(group => group.Key).ToList();
        StructuralJson.RequireKnownKeys(assigned.Keys, keys, "types");
        StructuralJson.RequireKnownKeys(heights.Keys, keys, "heightsMm");

        var existing = BeamPlacer.CollectExistingBeamLines(document);
        var available = BeamTypeMatcher.CollectBeamTypes(document);
        var items = new List<PlanItem>();
        var alreadyModeled = new List<string>();
        var total = 0;
        foreach (var group in groups)
        {
            var first = group.First();
            var hMm = first.HMm ?? (heights.TryGetValue(group.Key, out var h) ? h : (double?)null);
            var (symbol, family, typeText, problem) = ChooseType(document, group.Key, first.BMm, hMm, assigned, available, templateFamily);

            var beams = new List<object>();
            var duplicates = 0;
            foreach (var beam in group)
            {
                var line = new BeamPlacer.BeamLine(beam.Start, beam.End);
                if (existing.Any(other => BeamPlacer.AreDuplicate(other, line)))
                {
                    duplicates++;
                    continue;
                }
                beams.Add(new
                {
                    x0 = UnitConvert.Length(beam.Start.X), y0 = UnitConvert.Length(beam.Start.Y),
                    x1 = UnitConvert.Length(beam.End.X), y1 = UnitConvert.Length(beam.End.Y),
                    at = beam.At,
                });
            }
            if (beams.Count == 0)
            {
                alreadyModeled.Add(group.Key);
                continue;
            }
            total += beams.Count;

            var reasons = new List<string>();
            if (problem is not null) reasons.Add(problem);
            if (first.Name is null) reasons.Add("Không có ký hiệu dầm (Text Note hay chữ trong file DWG) gần các dầm này.");
            var inherited = group.Count(beam => beam.Beam.TagInherited);
            if (inherited > 0) reasons.Add($"{inherited} dầm lấy ký hiệu từ dầm thẳng hàng bên cạnh.");
            var widthNotes = group.Where(beam => beam.WidthNote is not null).ToList();
            if (widthNotes.Count > 0)
                reasons.Add($"{widthNotes.Count} dầm có {widthNotes[0].WidthNote}: dùng theo ký hiệu. {List(widthNotes.Select(beam => beam.At))}");
            var offGrid = group.Where(beam => !beam.OnGrid).ToList();
            if (offGrid.Count > 0) reasons.Add($"{offGrid.Count} dầm không nằm trên trục nào: {List(offGrid.Select(beam => beam.At))}");
            var broken = group.Where(beam => beam.Beam.Coverage < WellDrawn).ToList();
            if (broken.Count > 0)
                reasons.Add($"{broken.Count} dầm nét vẽ đứt nhiều (phủ dưới {WellDrawn:P0} nhịp), kiểm tra: {List(broken.Select(beam => beam.At))}");
            if (duplicates > 0) reasons.Add($"{duplicates} dầm đã có trong model, bỏ qua.");
            var weighed = group.Where(beam => weights.ContainsKey(beam.Beam)).Select(beam => weights[beam.Beam]).ToList();
            var evidenceSure = weighed.All(weight => weight.Sure);
            if (weighed.Count > 0)
            {
                if (!evidenceSure) reasons.Add($"{weighed.Count(weight => !weight.Sure)} dầm phụ bằng chứng chưa đủ, nên xem lại trên mặt bằng.");
                reasons.Add("Vì sao: " + string.Join("; ", weighed.SelectMany(weight => weight.Reasons)
                    .GroupBy(reason => reason).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} ({g.Count()}/{weighed.Count})")));
            }

            var spans = group.Select(beam => beam.At).ToList();
            items.Add(new PlanItem
            {
                ItemId = "B-" + group.Key.Replace(' ', '_'),
                Action = "create_beams",
                Params = JsonSerializer.SerializeToElement(new
                {
                    summary = $"{(first.Name is null ? "" : first.Name + " · ")}{Size(first.BMm, hMm)} · {beams.Count} dầm · {typeText} · {List(spans)}",
                    groupKey = group.Key,
                    mark = writeMark ? first.Name : null,
                    levelId = RevitJson.Id(level.Id),
                    offsetMm,
                    typeId = symbol is null ? null : RevitJson.Id(symbol.Id),
                    templateFamilyId = symbol is null && family is not null ? RevitJson.Id(family.Id) : null,
                    sizeBMm = first.BMm,
                    sizeHMm = hMm,
                    // Beam axes in model coordinates (mm), centre to centre of the supports; Z is the level plus offsetMm.
                    beams,
                }),
                Tier = problem is null ? PlanTier.Review : PlanTier.Blocked,
                Confidence = ConfidenceRules.Beams(group.Count(), first.Name is not null && first.HMm is not null,
                    inherited, widthNotes.Count, broken.Count, evidenceSure),
                Reasons = reasons,
                Source = JsonSerializer.SerializeToElement(new { name = group.Key, category = "Structural Framing", count = beams.Count }),
            });
        }
        if (total > context.Settings.MaxElementsPerWrite)
            throw RpcException.InvalidParams("cadId",
                $"{total} beams found, above the limit of {context.Settings.MaxElementsPerWrite} per call set in BIMTECH MCP "
                + "Settings. Ask the user to raise the limit.");

        var report = new
        {
            supports = new { columns = supports.Count(s => s.IsColumn), walls = supports.Count(s => s.Kind == "wall"), beams = supports.Count(s => s.IsBeam) },
            secondaryBeams = new { found = weights.Count, sure = weights.Values.Count(weight => weight.Sure) },
            dangling = new
            {
                count = result.Dangling.Count,
                list = result.Dangling.Take(MaxReported).Select(d =>
                {
                    var fit = FitCross(d.AlongU, d.Cross, 0);
                    return $"từ {d.From.Label}{(fit.OnGrid ? $" dọc trục {fit.GridName}" : "")}, {d.LengthMm:0} mm";
                }).ToList(),
            },
            unusedMarks = new { count = result.UnusedTags.Count, texts = result.UnusedTags.Take(MaxReported).Select(tag => tag.Text).ToList() },
            skewedLines = result.SkewedLines,
            linesInsideSupports = result.MaskedLines,
        };
        if (items.Count == 0)
            return new
            {
                plan = (object?)null,
                alreadyModeledGroups = alreadyModeled,
                report,
                dwg,
                note = result.Beams.Count == 0
                    ? "No beam was found between the columns. Check that the CAD beam lines are visible in this view, the columns "
                      + "below this level are modeled, and the layers (if given) hold the beam edges."
                    : "Every beam found already exists in the model. No plan was created.",
            };

        var plan = plans.Create(
            PlanStorageLocation.For(document),
            kind: "create_beams",
            title: $"Dựng dầm {(secondary ? "phụ" : "chính")} {level.Name}, {items.Count} nhóm",
            scope: JsonSerializer.SerializeToElement(new
            {
                cadId = RevitJson.Id(cad.Id),
                level = StructuralJson.Ref(level),
                offsetMm,
                layers,
                alreadyModeledGroups = alreadyModeled,
                report,
            }),
            items: items,
            modelStamp: changes.Current(document));
        review.Show(plan, context.UIApplication);

        return new
        {
            plan = PlanResults.Status(plan),
            beams = total,
            groups = items.Count,
            blockedGroups = items.Where(item => item.Tier == PlanTier.Blocked).Select(item => new { item.ItemId, reason = item.Reasons.FirstOrDefault() }),
            alreadyModeledGroups = alreadyModeled,
            report,
            dwg,
            reviewWindow = "opened",
            note = "Nothing was changed in the model. Each item is one beam group. Blocked groups need the user's answer "
                + "(heightsMm or types). Tell the user about dangling edges (secondary beams or consoles, not placed yet) and "
                + "unused marks. Ask the user to review in Revit and say when they are done, then call get_plan_status and commit_plan.",
        };
    }

    private static (FamilySymbol? Symbol, Family? Family, string Text, string? Problem) ChooseType(
        Document document, string key, double bMm, double? hMm, IReadOnlyDictionary<string, string> assigned,
        IReadOnlyList<FamilySymbol> available, Family? templateFamily)
    {
        if (assigned.TryGetValue(key, out var typeId))
        {
            var symbol = StructuralJson.RequireType(document, typeId, $"types.{key}", BuiltInCategory.OST_StructuralFraming, "propose_beams");
            return (symbol, null, $"Type {symbol.Name}", null);
        }
        if (hMm is not { } h)
            return (null, null, "chưa có Type",
                $"Chưa biết chiều cao h của dầm b={bMm:0}: ký hiệu không ghi kích thước. Hỏi người dùng rồi truyền heightsMm hoặc types.");
        if (BeamTypeMatcher.FindExactMatch(bMm, h, available, s => s) is { } exact)
            return (exact, null, $"Type {exact.Name} ({exact.FamilyName})", null);
        if (templateFamily is not null)
            return (null, templateFamily, $"tạo Type {Size(bMm, h)} trong {templateFamily.Name}", null);
        return (null, null, "chưa có Type",
            $"Chưa có Type dầm {Size(bMm, h)}: gán trong 'types' hoặc truyền templateFamilyId (load_default_family kind=beam).");
    }

    private static string Size(double bMm, double? hMm) => hMm is { } h ? $"{bMm:0}x{h:0}" : $"b{bMm:0}";

    private static string List(IEnumerable<string> names)
    {
        var list = names.ToList();
        return string.Join(", ", list.Take(MaxListed)) + (list.Count > MaxListed ? "..." : "");
    }

    private static double RoundTo5(double mm) => Math.Round(mm / 5, MidpointRounding.AwayFromZero) * 5;

    private static double Feet(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
}
