using Autodesk.Revit.DB;
using View = Autodesk.Revit.DB.View;

namespace Bimtech.StructuralCore.Walls;

/// <summary>Gộp nhóm tường theo tên đọc từ Text + bề dày, khớp/tạo Basic Wall Type, đặt Wall thật giữa Level đáy/đỉnh.
/// Phần tim CAD đã có Wall cùng trục bị trừ đi, chỉ tạo đoạn còn thiếu. Mỗi thao tác ghi có 1 hàm Plan* chỉ đọc model.</summary>
internal static class WallPlacer
{
    private const double MatchToleranceMm = 2.0;
    private const double DuplicatePositionToleranceMm = 10.0;
    private const double DuplicateAngleToleranceDegrees = 1.0;
    private const double MinimumRemainingLengthMm = 50.0;
    private const double MergeAngleToleranceDegrees = 2.0;
    private static readonly double MergePerpendicularToleranceFeet = UnitUtils.ConvertToInternalUnits(20.0, UnitTypeId.Millimeters);
    private static readonly double MergeAdjacentGapFeet = UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Millimeters);
    internal const string NoTagParameterLabel = "(Không gán Shared Parameter)";
    internal readonly record struct WallLine(XYZ Start, XYZ End);

    /// <summary>Mọi Basic Wall Type trong project.</summary>
    public static List<WallType> CollectBasicWallTypes(Document doc) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(WallType))
            .Cast<WallType>()
            .Where(s => s.Kind == WallKind.Basic)
            .ToList();

    /// <summary>Gộp tim thẳng hàng rồi chia nhóm theo tên Text + bề dày làm tròn 5mm. Nhóm có tên trước, rồi theo bề dày.</summary>
    public static List<WallSizeGroup> GroupWalls(IEnumerable<RawWall> walls)
    {
        // Union tim theo hình học trước khi TagName/bề dày chia chúng thành các nhóm.
        // Nhờ vậy một tim ngắn nằm trong tim dài không thể sống sót chỉ vì đọc được tag khác.
        var mergedWalls = MergeCollinearWalls(walls.ToList());

        return mergedWalls.GroupBy(wall =>
        {
            var width = Math.Round(UnitUtils.ConvertFromInternalUnits(wall.WidthFeet, UnitTypeId.Millimeters) / 5) * 5;
            return $"{wall.TagName?.ToUpperInvariant() ?? "(NO-TAG)"}|W:{width}";
        })
            .Select(g =>
            {
                var first = g.First();
                var width = Math.Round(UnitUtils.ConvertFromInternalUnits(first.WidthFeet, UnitTypeId.Millimeters) / 5) * 5;
                return new WallSizeGroup { TagName = first.TagName, SizeBMm = width, Walls = g.ToList() };
            }).OrderBy(g => g.TagName is null).ThenBy(g => g.SizeBMm).ToList();
    }

    /// <summary>Type gợi ý cho 1 nhóm: Type khớp đúng bề dày (IsExact = true), không thì Type có bề dày gần nhất.</summary>
    public static (T? Choice, bool IsExact) SuggestType<T>(double widthMm, IReadOnlyList<T> choices, Func<T, WallType> typeOf)
        where T : class
    {
        var exact = choices.FirstOrDefault(choice => IsMatch(typeOf(choice), widthMm));
        if (exact is not null)
            return (exact, true);
        var nearest = choices.OrderBy(c => Math.Abs(UnitUtils.ConvertFromInternalUnits(typeOf(c).Width, UnitTypeId.Millimeters) - widthMm)).FirstOrDefault();
        return (nearest, false);
    }

    public static bool IsMatch(WallType type, double widthMm) =>
        Math.Abs(UnitUtils.ConvertFromInternalUnits(type.Width, UnitTypeId.Millimeters) - widthMm) <= MatchToleranceMm;

    /// <summary>Union các tim tường CAD cùng trục, chồng lấn hoặc liền nhau. Phỏng theo
    /// PlaceFloor.CenterlineMerger nhưng giữ lại metadata/segment nguồn của RawWall.</summary>
    internal static List<RawWall> MergeCollinearWalls(IReadOnlyList<RawWall> walls)
    {
        // Xét tim dài trước để đoạn bao phủ lớn nhất quyết định trục và bề dày.
        var orderedWalls = walls
            .OrderByDescending(wall => wall.Start.DistanceTo(wall.End))
            .ToList();
        var used = new bool[orderedWalls.Count];
        var result = new List<RawWall>();

        for (var i = 0; i < orderedWalls.Count; i++)
        {
            if (used[i]) continue;
            used[i] = true;
            var source = orderedWalls[i];
            var p0 = source.Start;
            var p1 = source.End;
            var vector = p1 - p0;
            if (vector.IsZeroLength()) continue;
            var direction = vector.Normalize();
            var members = new List<RawWall> { source };

            var mergedAny = true;
            while (mergedAny)
            {
                mergedAny = false;
                for (var j = 0; j < orderedWalls.Count; j++)
                {
                    if (used[j] || !TryUnion(p0, p1, direction, orderedWalls[j], out var newP0, out var newP1))
                        continue;
                    p0 = newP0; p1 = newP1;
                    used[j] = true; members.Add(orderedWalls[j]); mergedAny = true;
                }
            }

            var metadataSource = members
                .Where(member => !string.IsNullOrWhiteSpace(member.TagName) || member.TagHMm.HasValue)
                .OrderByDescending(member => !string.IsNullOrWhiteSpace(member.TagName))
                .ThenByDescending(member => member.Start.DistanceTo(member.End))
                .FirstOrDefault() ?? source;

            result.Add(new RawWall
            {
                Start = p0,
                End = p1,
                WidthFeet = source.WidthFeet,
                Segments = members.SelectMany(member => member.Segments).ToList(),
                TagName = metadataSource.TagName,
                TagBMm = metadataSource.TagBMm,
                TagHMm = metadataSource.TagHMm,
                IsCrossLayerPair = members.Any(member => member.IsCrossLayerPair),
                CrossLayerNote = members.Select(member => member.CrossLayerNote).FirstOrDefault(note => !string.IsNullOrWhiteSpace(note)),
            });
        }
        return result;
    }

    private static bool TryUnion(XYZ p0, XYZ p1, XYZ direction, RawWall candidate, out XYZ newP0, out XYZ newP1)
    {
        newP0 = p0; newP1 = p1;
        var candidateVector = candidate.End - candidate.Start;
        if (candidateVector.IsZeroLength()) return false;
        var candidateDirection = candidateVector.Normalize();
        var angle = Math.Acos(Math.Clamp(Math.Abs(direction.DotProduct(candidateDirection)), -1.0, 1.0)) * 180.0 / Math.PI;
        if (angle > MergeAngleToleranceDegrees) return false;

        var offset = candidate.Start - p0;
        var projectedOffset = offset.DotProduct(direction);
        var perpendicular = offset - projectedOffset * direction;
        if (perpendicular.GetLength() > MergePerpendicularToleranceFeet) return false;

        var currentEnd = (p1 - p0).DotProduct(direction);
        var currentMin = Math.Min(0.0, currentEnd);
        var currentMax = Math.Max(0.0, currentEnd);
        var candidateT0 = (candidate.Start - p0).DotProduct(direction);
        var candidateT1 = (candidate.End - p0).DotProduct(direction);
        var candidateMin = Math.Min(candidateT0, candidateT1);
        var candidateMax = Math.Max(candidateT0, candidateT1);
        var gap = Math.Max(currentMin, candidateMin) - Math.Min(currentMax, candidateMax);
        if (gap > MergeAdjacentGapFeet) return false;

        var unionMin = Math.Min(currentMin, candidateMin);
        var unionMax = Math.Max(currentMax, candidateMax);
        newP0 = p0 + unionMin * direction;
        newP1 = p0 + unionMax * direction;
        return true;
    }

    // ------------------------------------------------------------------ Tạo Wall Type còn thiếu

    /// <summary>Xem trước bước tạo Type, không sửa model: nhóm nào dùng lại Type cùng bề dày (kể cả Type sắp tạo cho nhóm
    /// trước), nhóm nào tạo Type mới "BTC-VachKC_b".</summary>
    public static List<(WallSizeGroup Group, WallType? Reuse, string? NewTypeName)> PlanMissingTypes(
        IEnumerable<WallSizeGroup> groups, IReadOnlyList<WallType> pool)
    {
        var names = pool.Select(type => type.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var planned = new List<(double Width, string Name)>();
        var previews = new List<(WallSizeGroup, WallType?, string?)>();
        foreach (var group in groups)
        {
            var reuse = pool.FirstOrDefault(type => IsMatch(type, group.SizeBMm));
            if (reuse is not null)
            {
                previews.Add((group, reuse, null));
                continue;
            }
            var shared = planned.FirstOrDefault(p => Math.Abs(p.Width - group.SizeBMm) <= MatchToleranceMm);
            if (shared.Name is not null)
            {
                previews.Add((group, null, shared.Name));
                continue;
            }
            var name = SymbolParameters.UniqueName($"BTC-VachKC_{Math.Round(group.SizeBMm):0}", names);
            names.Add(name);
            planned.Add((group.SizeBMm, name));
            previews.Add((group, null, name));
        }
        return previews;
    }

    /// <summary>Tạo Wall Type cho các nhóm cho trước (caller chỉ truyền nhóm CẦN DỰNG và CHƯA khớp đúng Type): dùng lại
    /// Type cùng bề dày trong <paramref name="pool"/> (kể cả Type vừa tạo), không thì duplicate Type mẫu, đặt tên
    /// "BTC-VachKC_b" và chỉnh lớp kết cấu cho đúng tổng bề dày.</summary>
    public static WallCreateTypesSummary CreateMissingTypes(
        Document doc, IEnumerable<WallSizeGroup> groups, WallType templateType, IReadOnlyList<WallType> pool)
    {
        var created = 0; var failed = 0;
        var errors = new List<string>(); var createdTypes = new List<WallType>();
        var resolved = new Dictionary<WallSizeGroup, WallType>();
        var choices = pool.GroupBy(type => type.Id).Select(group => group.First()).ToList();
        using var tx = new Transaction(doc, "Tạo Wall Type còn thiếu"); tx.Start();

        foreach (var group in groups)
        {
            var reuse = choices.FirstOrDefault(choice => IsMatch(choice, group.SizeBMm));
            if (reuse is not null)
            {
                resolved[group] = reuse; continue;
            }

            try
            {
                var existingNames = choices
                    .Select(choice => choice.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var typeName = SymbolParameters.UniqueName($"BTC-VachKC_{Math.Round(group.SizeBMm):0}", existingNames);

                var newType = (WallType)templateType.Duplicate(typeName);
                ResizeWallType(newType, group.SizeBMm);
                choices.Add(newType); createdTypes.Add(newType);
                resolved[group] = newType; created++;
            }
            catch (Exception ex)
            {
                failed++;
                if (errors.Count < 20) errors.Add($"Tường {group.SizeBMm:0}mm: {ex.Message}");
            }
        }
        tx.Commit();
        return new(created, failed, errors, createdTypes, resolved);
    }

    /// <summary>Đổi tổng bề dày của Wall Type bằng cách chỉnh lớp Structure dày nhất (hoặc lớp dày nhất nếu không có lớp Structure).</summary>
    internal static void ResizeWallType(WallType wallType, double targetWidthMm)
    {
        var structure = wallType.GetCompoundStructure()
            ?? throw new InvalidOperationException("Wall Type mẫu không có cấu tạo lớp.");
        var layers = structure.GetLayers();
        if (layers.Count == 0) throw new InvalidOperationException("Wall Type mẫu không có lớp vật liệu.");
        var index = Enumerable.Range(0, layers.Count)
            .Where(i => layers[i].Function == MaterialFunctionAssignment.Structure)
            .OrderByDescending(i => layers[i].Width).Cast<int?>().FirstOrDefault()
            ?? Enumerable.Range(0, layers.Count).OrderByDescending(i => layers[i].Width).First();
        var target = UnitUtils.ConvertToInternalUnits(targetWidthMm, UnitTypeId.Millimeters);
        var otherWidth = layers.Where((_, i) => i != index).Sum(layer => layer.Width);
        var layerWidth = target - otherWidth;
        if (layerWidth <= UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Millimeters))
            throw new InvalidOperationException("Các lớp còn lại của Type mẫu đã dày hơn kích thước cần tạo.");
        structure.SetLayerWidth(index, layerWidth);
        wallType.SetCompoundStructure(structure);
    }

    // ------------------------------------------------------------------ Đặt tường

    private static double HeightFeet(Level? baseLevel, Level? topLevel, double baseOffsetFeet, double topOffsetFeet) =>
        baseLevel is null || topLevel is null
            ? 0
            : topLevel.Elevation + topOffsetFeet - baseLevel.Elevation - baseOffsetFeet;

    /// <summary>Xem trước bước Đặt, không sửa model. Đi đúng thứ tự của Place: thiếu Level/Type hoặc chiều cao ≤ 0 → lỗi;
    /// trừ phần đã có Wall → bỏ qua nếu không còn gì, không thì đếm số đoạn sẽ tạo (đoạn sắp tạo cũng được tính vào kiểm tra trùng).</summary>
    public static List<WallPlacementPreview> PlanPlacement(
        Document doc, IReadOnlyList<WallAssignment> assignments, Level? baseLevel, Level? topLevel,
        double baseOffsetFeet, double topOffsetFeet)
    {
        var existing = CollectExistingWallLines(doc);
        var height = HeightFeet(baseLevel, topLevel, baseOffsetFeet, topOffsetFeet);
        var previews = new List<WallPlacementPreview>();

        foreach (var (group, type) in assignments)
        {
            if (baseLevel is null || topLevel is null || height <= 0 || type is null)
            {
                previews.Add(new WallPlacementPreview(group, 0, 0, 0,
                    type is null ? "chưa gán Wall Type" : "Level đỉnh phải cao hơn Level đáy (tính cả offset)"));
                continue;
            }

            int walls = 0, pieces = 0, covered = 0;
            var baseElevation = baseLevel.Elevation + baseOffsetFeet;
            foreach (var raw in group.Walls)
            {
                var line = new WallLine(new(raw.Start.X, raw.Start.Y, baseElevation), new(raw.End.X, raw.End.Y, baseElevation));
                var remaining = SubtractExisting(line, existing);
                if (remaining.Count == 0) { covered++; continue; }
                walls++;
                pieces += remaining.Count;
                existing.AddRange(remaining);
            }
            previews.Add(new WallPlacementPreview(group, walls, pieces, covered, null));
        }

        return previews;
    }

    /// <summary>Đặt tường của các nhóm cho trước (caller chỉ truyền nhóm cần dựng) trong 1 Transaction. Phần tim đã có Wall
    /// cùng trục bị trừ đi, chỉ tạo đoạn còn thiếu ≥ 50mm. Ghi tên nhóm vào Shared Parameter nếu có chọn.</summary>
    public static PlaceSummary Place(
        Document doc, IReadOnlyList<WallAssignment> assignments, Level? baseLevel, Level? topLevel,
        double baseOffsetFeet, double topOffsetFeet, string? tagParameterName)
    {
        using var tx = new Transaction(doc, "Đặt Tường theo CAD"); tx.Start();
        var summary = PlaceInTransaction(doc, assignments, baseLevel, topLevel, baseOffsetFeet, topOffsetFeet, tagParameterName);
        tx.Commit();
        return summary;
    }

    /// <summary>Như Place nhưng chạy trong Transaction do caller mở sẵn (khung plan của MCP: mỗi mục 1 Transaction).</summary>
    public static PlaceSummary PlaceInTransaction(
        Document doc, IReadOnlyList<WallAssignment> assignments, Level? baseLevel, Level? topLevel,
        double baseOffsetFeet, double topOffsetFeet, string? tagParameterName)
    {
        var created = 0; var failed = 0; var skipped = 0;
        var errors = new List<string>(); var ids = new List<ElementId>();
        var existing = CollectExistingWallLines(doc);
        var height = HeightFeet(baseLevel, topLevel, baseOffsetFeet, topOffsetFeet);
        foreach (var (group, type) in assignments)
        foreach (var raw in group.Walls)
        {
            if (baseLevel is null || topLevel is null || height <= 0 || type is null) { failed++; continue; }
            var baseElevation = baseLevel.Elevation + baseOffsetFeet;
            var line = new WallLine(new(raw.Start.X, raw.Start.Y, baseElevation), new(raw.End.X, raw.End.Y, baseElevation));
            var remainingLines = SubtractExisting(line, existing);
            if (remainingLines.Count == 0) { skipped++; continue; }

            foreach (var remainingLine in remainingLines)
            {
                try
                {
                    var creationLine = Line.CreateBound(
                        new XYZ(remainingLine.Start.X, remainingLine.Start.Y, baseLevel.Elevation),
                        new XYZ(remainingLine.End.X, remainingLine.End.Y, baseLevel.Elevation));
                    var wall = Wall.Create(
                        doc, creationLine, type.Id,
                        baseLevel.Id, height, baseOffsetFeet, false, true);
                    wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.Set(topLevel.Id);
                    wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET)?.Set(topOffsetFeet);
                    if (!string.IsNullOrWhiteSpace(tagParameterName) && !string.IsNullOrWhiteSpace(group.TagName))
                    {
                        var p = wall.LookupParameter(tagParameterName);
                        if (p is { IsReadOnly: false, StorageType: StorageType.String }) p.Set(group.TagName);
                    }
                    created++; ids.Add(wall.Id); existing.Add(remainingLine);
                }
                catch (Exception ex) { failed++; if (errors.Count < 20) errors.Add(ex.Message); }
            }
        }
        return new(created, failed, skipped, errors, ids);
    }

    /// <summary>Type mẫu mặc định để tạo Type còn thiếu: Basic Wall có cấu tạo lớp, bề dày gần <paramref name="widthMm"/> nhất.</summary>
    public static WallType? NearestTemplateType(IReadOnlyList<WallType> pool, double widthMm) =>
        pool.Where(type => type.GetCompoundStructure() is not null)
            .OrderBy(type => Math.Abs(UnitUtils.ConvertFromInternalUnits(type.Width, UnitTypeId.Millimeters) - widthMm))
            .FirstOrDefault();

    /// <summary>Type cho 1 nhóm trong Transaction đang mở: dùng lại Basic Wall Type cùng bề dày (kể cả Type vừa tạo ở mục
    /// trước), không thì duplicate <paramref name="templateType"/> thành "BTC-VachKC_b". Created = true khi vừa tạo.</summary>
    public static (WallType Type, bool Created) ResolveOrCreateType(Document doc, double widthMm, WallType templateType)
    {
        var pool = CollectBasicWallTypes(doc);
        var reuse = pool.FirstOrDefault(type => IsMatch(type, widthMm));
        if (reuse is not null) return (reuse, false);

        var names = pool.Select(type => type.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newType = (WallType)templateType.Duplicate(SymbolParameters.UniqueName($"BTC-VachKC_{Math.Round(widthMm):0}", names));
        ResizeWallType(newType, widthMm);
        return (newType, true);
    }

    /// <summary>Hai tim tường được coi là trùng khi cùng cao độ, gần song song, nằm trên cùng trục
    /// trong sai số và có đoạn chồng lấn. Tái sử dụng cùng nguyên tắc chống trùng của PlaceWall.</summary>
    internal static bool AreDuplicate(WallLine existing, WallLine candidate)
    {
        var toleranceFeet = UnitUtils.ConvertToInternalUnits(DuplicatePositionToleranceMm, UnitTypeId.Millimeters);
        if (Math.Abs(existing.Start.Z - candidate.Start.Z) > toleranceFeet ||
            Math.Abs(existing.End.Z - candidate.End.Z) > toleranceFeet)
            return false;

        var existingVector = new XYZ(existing.End.X - existing.Start.X, existing.End.Y - existing.Start.Y, 0);
        var candidateVector = new XYZ(candidate.End.X - candidate.Start.X, candidate.End.Y - candidate.Start.Y, 0);
        var existingLength = existingVector.GetLength();
        var candidateLength = candidateVector.GetLength();
        if (existingLength <= toleranceFeet || candidateLength <= toleranceFeet)
            return false;

        var existingDirection = existingVector / existingLength;
        var candidateDirection = candidateVector / candidateLength;
        var cross = Math.Abs(existingDirection.X * candidateDirection.Y - existingDirection.Y * candidateDirection.X);
        if (cross > Math.Sin(DuplicateAngleToleranceDegrees * Math.PI / 180.0))
            return false;

        double DistanceToExistingAxis(XYZ point)
        {
            var offsetX = point.X - existing.Start.X;
            var offsetY = point.Y - existing.Start.Y;
            return Math.Abs(offsetX * existingDirection.Y - offsetY * existingDirection.X);
        }

        if (DistanceToExistingAxis(candidate.Start) > toleranceFeet ||
            DistanceToExistingAxis(candidate.End) > toleranceFeet)
            return false;

        double ProjectOnExistingAxis(XYZ point) =>
            (point.X - existing.Start.X) * existingDirection.X +
            (point.Y - existing.Start.Y) * existingDirection.Y;

        var candidateMin = Math.Min(ProjectOnExistingAxis(candidate.Start), ProjectOnExistingAxis(candidate.End));
        var candidateMax = Math.Max(ProjectOnExistingAxis(candidate.Start), ProjectOnExistingAxis(candidate.End));
        var overlap = Math.Min(existingLength, candidateMax) - Math.Max(0.0, candidateMin);
        return overlap > toleranceFeet;
    }

    /// <summary>Trừ khỏi tim CAD toàn bộ khoảng đã được các Wall thẳng cùng trục che phủ.
    /// Một candidate có thể trả về 0, 1 hoặc nhiều đoạn còn thiếu để tạo.</summary>
    internal static List<WallLine> SubtractExisting(WallLine candidate, IReadOnlyList<WallLine> existingLines)
    {
        var positionTolerance = UnitUtils.ConvertToInternalUnits(DuplicatePositionToleranceMm, UnitTypeId.Millimeters);
        var minimumLength = UnitUtils.ConvertToInternalUnits(MinimumRemainingLengthMm, UnitTypeId.Millimeters);
        var vector = new XYZ(candidate.End.X - candidate.Start.X, candidate.End.Y - candidate.Start.Y, 0);
        var length = vector.GetLength();
        if (length <= minimumLength)
            return new List<WallLine>();

        var direction = vector / length;
        var blocked = new List<(double Start, double End)>();

        foreach (var existing in existingLines)
        {
            if (Math.Abs(existing.Start.Z - candidate.Start.Z) > positionTolerance ||
                Math.Abs(existing.End.Z - candidate.End.Z) > positionTolerance)
                continue;

            var existingVector = new XYZ(existing.End.X - existing.Start.X, existing.End.Y - existing.Start.Y, 0);
            var existingLength = existingVector.GetLength();
            if (existingLength <= positionTolerance)
                continue;

            var existingDirection = existingVector / existingLength;
            var cross = Math.Abs(direction.X * existingDirection.Y - direction.Y * existingDirection.X);
            if (cross > Math.Sin(DuplicateAngleToleranceDegrees * Math.PI / 180.0))
                continue;

            var existingMid = (existing.Start + existing.End) / 2.0;
            var midOffsetX = existingMid.X - candidate.Start.X;
            var midOffsetY = existingMid.Y - candidate.Start.Y;
            var distanceToAxis = Math.Abs(midOffsetX * direction.Y - midOffsetY * direction.X);
            if (distanceToAxis > positionTolerance)
                continue;

            double Project(XYZ point) =>
                (point.X - candidate.Start.X) * direction.X +
                (point.Y - candidate.Start.Y) * direction.Y;

            var start = Math.Max(0.0, Math.Min(Project(existing.Start), Project(existing.End)));
            var end = Math.Min(length, Math.Max(Project(existing.Start), Project(existing.End)));
            if (end - start > positionTolerance)
                blocked.Add((start, end));
        }

        if (blocked.Count == 0)
            return new List<WallLine> { candidate };

        blocked.Sort((left, right) => left.Start.CompareTo(right.Start));
        var merged = new List<(double Start, double End)>();
        foreach (var interval in blocked)
        {
            if (merged.Count == 0 || interval.Start > merged[^1].End + positionTolerance)
            {
                merged.Add(interval);
                continue;
            }

            var last = merged[^1];
            merged[^1] = (last.Start, Math.Max(last.End, interval.End));
        }

        var remaining = new List<WallLine>();
        var cursor = 0.0;
        foreach (var interval in merged)
        {
            AddRemaining(cursor, interval.Start);
            cursor = Math.Max(cursor, interval.End);
        }
        AddRemaining(cursor, length);
        return remaining;

        void AddRemaining(double start, double end)
        {
            if (end - start < minimumLength)
                return;
            remaining.Add(new WallLine(candidate.Start + direction * start, candidate.Start + direction * end));
        }
    }

    internal static List<WallLine> CollectExistingWallLines(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>()
            .Select(w => w.Location).OfType<LocationCurve>().Select(x => x.Curve).OfType<Line>()
            .Select(x => new WallLine(x.GetEndPoint(0), x.GetEndPoint(1))).ToList();
}

/// <summary>Quét tường đầy đủ như tool WPF: ghép cặp Line, sửa Line mồ côi, gán tên từ Text Note trong view; rồi (khi Đặt)
/// lọc theo layer/vùng, bỏ tường không có Wall/Foundation đỡ bên dưới, tuỳ chọn nối tới cột.</summary>
internal static class WallScanPipeline
{
    public const double DefaultTextSearchExpandMm = WallTagReader.DefaultSearchExpandMm;
    public const double DefaultMinOverlapFraction = 0.0;
    public const double DefaultMinPairLengthRatio = 0.70;
    public const double SupportSearchDepthMm = 500.0;
    public const double DefaultColumnSearchDistanceMm = 3000.0;

    public static CadWallScanResult ScanWithTags(
        Document doc, ImportInstance cad, View view, double textSearchExpandFeet, double minOverlapFraction, double minPairLengthRatio)
    {
        var textNotes = new FilteredElementCollector(doc, view.Id).OfClass(typeof(TextNote)).Cast<TextNote>().ToList();
        var scan = CadWallScanner.Scan(doc, cad, view, minOverlapFraction, minPairLengthRatio);

        var repaired = WallOrphanRepairer.Repair(scan.RawSegmentsByLayer, scan.Orphans, minPairLengthRatio);
        foreach (var (wall, layerName) in repaired)
        {
            if (scan.WallsByLayer.TryGetValue(layerName, out var list))
                list.Add(wall);
        }

        WallTagReader.AttachTags(scan.WallsByLayer.Values.SelectMany(list => list), textNotes, textSearchExpandFeet);
        return scan;
    }

    /// <summary>Bỏ tường không có Wall hoặc Structural Foundation đỡ trong 500mm bên dưới chân tường.</summary>
    public static List<RawWall> KeepSupported(Document doc, List<RawWall> walls, double baseElevationFeet) =>
        WallSupportFilter.Filter(doc, walls, baseElevationFeet,
            UnitUtils.ConvertToInternalUnits(SupportSearchDepthMm, UnitTypeId.Millimeters));
}

/// <summary>Wall Type mặc định của công ty — Basic Wall dày 200mm, nhân bản từ Type sẵn có gần 200mm nhất.</summary>
internal static class WallDefaults
{
    public const string DefaultWallTypeName = "BTC-VachKC_200";
    public const double DefaultWallThicknessMm = 200.0;

    /// <summary>Tạo Basic Wall Type mặc định dày 200mm từ một Type có cấu tạo lớp sẵn trong project — trả true nếu đã có
    /// hoặc tạo được. Mở Transaction riêng.</summary>
    public static bool EnsureDefaultWallType(Document doc, out string error)
    {
        error = "";
        var types = WallPlacer.CollectBasicWallTypes(doc);

        if (types.Any(type => string.Equals(type.Name, DefaultWallTypeName, StringComparison.OrdinalIgnoreCase)))
            return true;

        var seed = types
            .Where(type => type.GetCompoundStructure() is not null)
            .OrderBy(type => Math.Abs(UnitUtils.ConvertFromInternalUnits(type.Width, UnitTypeId.Millimeters) - DefaultWallThicknessMm))
            .FirstOrDefault();
        if (seed is null)
        {
            error = "Project không có Basic Wall Type phù hợp để làm Type mẫu.";
            return false;
        }

        using var tx = new Transaction(doc, "Tạo Wall Type mặc định");
        tx.Start();
        try
        {
            var newType = (WallType)seed.Duplicate(DefaultWallTypeName);
            var structure = newType.GetCompoundStructure()
                ?? throw new InvalidOperationException("Wall Type mẫu không có cấu tạo lớp.");
            var layers = structure.GetLayers();
            if (layers.Count == 0)
                throw new InvalidOperationException("Wall Type mẫu không có lớp vật liệu.");

            var adjustableIndex = Enumerable.Range(0, layers.Count)
                .Where(index => layers[index].Function == MaterialFunctionAssignment.Structure)
                .OrderByDescending(index => layers[index].Width)
                .Cast<int?>()
                .FirstOrDefault()
                ?? Enumerable.Range(0, layers.Count).OrderByDescending(index => layers[index].Width).First();

            var targetWidth = UnitUtils.ConvertToInternalUnits(DefaultWallThicknessMm, UnitTypeId.Millimeters);
            var otherWidth = layers.Where((_, index) => index != adjustableIndex).Sum(layer => layer.Width);
            var newLayerWidth = targetWidth - otherWidth;
            if (newLayerWidth <= UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Millimeters))
                throw new InvalidOperationException("Các lớp còn lại của Wall Type mẫu đã dày từ 200mm trở lên.");

            structure.SetLayerWidth(adjustableIndex, newLayerWidth);
            newType.SetCompoundStructure(structure);
            tx.Commit();
            return true;
        }
        catch (Exception ex)
        {
            tx.RollBack();
            error = ex.Message;
            return false;
        }
    }
}
