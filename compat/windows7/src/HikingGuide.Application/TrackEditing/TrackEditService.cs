using HikingGuide.Application.Guides;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;

namespace HikingGuide.Application.TrackEditing;

/// <summary>只在 Prepare 中构造候选几何；不会写原始文件或修改项目。</summary>
public sealed class TrackEditService
{
    public double MergeToleranceMeters { get; init; } = 0.01;
    private static void RequireOrdinaryTrack(HikingProject project, Guid trackId)
    {
        if (project.MainGuideTrackId == trackId)
            throw new InvalidOperationException("主攻略轨迹及其拆分分段不可合并、连接或拆分。请先转为普通轨迹，攻略节点将同步转为普通标注。");
    }
    private static readonly IReadOnlyDictionary<TrackEdgeKey, IReadOnlyList<TrackEdgeKey>> NoMappings =
        new Dictionary<TrackEdgeKey, IReadOnlyList<TrackEdgeKey>>();
    private static readonly IReadOnlySet<Guid> NoRemovedSegments = new HashSet<Guid>();
    private readonly TrackSnapService _fingerprints = new();
    private readonly GuideNodeGeometryReconciler _reconciler = new();
    private readonly GuideStructureService _guides = new();

    public TrackEditPlan PrepareCombineSplitSections(HikingProject project, Guid trackId,
        IReadOnlyList<SplitSectionSource> orderedSources, bool connect,
        CancellationToken cancellationToken = default)
    {
        RequireOrdinaryTrack(project, trackId);
        var track = FindTrack(project, trackId);
        var sections = track.GetSplitSections();
        if (orderedSources.Count < 2 || orderedSources.Select(source => source.SectionId).Distinct().Count() != orderedSources.Count)
            throw new InvalidOperationException("请至少选择两个不同的拆分分段。");
        var byId = sections.ToDictionary(section => section.Id);
        if (orderedSources.Any(source => !byId.ContainsKey(source.SectionId)))
            throw new InvalidOperationException("所选拆分分段不属于当前轨迹。");
        var selected = orderedSources.Select(source => (Source: source, Section: byId[source.SectionId])).ToArray();
        var mergedId = Guid.NewGuid();
        var mergedName = NextCombinedSectionName(sections, connect);
        string? mergedDescription = string.Join("\n\n", selected.Select(item =>
        {
            var description = item.Section.GeometryParts.FirstOrDefault()?.SplitSectionDescription;
            return string.IsNullOrWhiteSpace(description) ? null : $"{item.Section.Name}：{description}";
        }).Where(description => description is not null));
        if (mergedDescription.Length == 0) mergedDescription = null;
        var mergedParts = new List<TrackSegment>();
        GeoCoordinate? previousEnd = null;
        var gapDescriptions = new List<string>();
        foreach (var (source, section) in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parts = source.Reversed
                ? section.GeometryParts.Reverse().Select(part =>
                {
                    var copy = CopySegment(part);
                    copy.Points.Reverse();
                    return copy;
                }).ToArray()
                : section.GeometryParts.Select(CopySegment).ToArray();
            var start = parts.First().StartPoint!.Coordinate;
            if (previousEnd is { } from && connect)
            {
                var length = GeoDistanceCalculator.Instance.CalculateDistance(from, start);
                gapDescriptions.Add($"人工直线连接 {length:F1} 米");
                mergedParts.Add(new TrackSegment
                {
                    Name = "人工直线连接", SplitSectionId = mergedId,
                    SplitSectionName = mergedName, SplitSectionDescription = mergedDescription,
                    OriginalAttributes = new Dictionary<string, string?>
                    { ["hg:editorProvenance"] = "人工直线连接" },
                    Points = [NewPoint(from), NewPoint(start)]
                });
            }
            foreach (var part in parts)
            {
                part.SplitSectionId = mergedId;
                part.SplitSectionName = mergedName;
                part.SplitSectionDescription = mergedDescription;
                mergedParts.Add(part);
            }
            previousEnd = parts.Last().EndPoint!.Coordinate;
        }
        var firstIndex = sections.Where(section => orderedSources.Any(source => source.SectionId == section.Id))
            .Min(section => section.DisplayOrder);
        var selectedIds = orderedSources.Select(source => source.SectionId).ToHashSet();
        var result = new List<TrackSegment>();
        foreach (var section in sections)
        {
            if (section.DisplayOrder == firstIndex) result.AddRange(mergedParts);
            if (!selectedIds.Contains(section.Id)) result.AddRange(section.GeometryParts);
        }
        return BuildPlan(project, track, connect ? "连接相邻分段" : "合并相邻分段",
            result, NoMappings, NoRemovedSegments, true, connect, cancellationToken,
            connect ? ["连接几何为人工直线，不代表道路或可通行路线；未使用 BRouter。", .. gapDescriptions] :
                ["合并后的多个几何 Part 保持原有间隙；间隙不计入轨迹里程。"]);
    }

    private static string NextCombinedSectionName(IReadOnlyList<SplitSection> sections, bool connect)
    {
        var prefix = connect ? "连接后的分段" : "合并后的分段";
        var maximum = 0;
        foreach (var section in sections)
        {
            if (!section.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var suffix = section.Name[prefix.Length..].Trim();
            if (int.TryParse(suffix, out var number) && number > maximum) maximum = number;
        }
        return $"{prefix}{maximum + 1}";
    }

    public TrackEditPlan PrepareReverseSplitSection(HikingProject project, Guid trackId,
        Guid sectionId, CancellationToken token = default)
    {
        var track = FindTrack(project, trackId);
        var section = track.GetSplitSections().FirstOrDefault(item => item.Id == sectionId)
            ?? throw new InvalidOperationException("所选拆分分段不存在。");
        var reversed = section.GeometryParts.Reverse().Select(part =>
        {
            var copy = CopySegment(part);
            copy.Points.Reverse();
            return copy;
        }).ToArray();
        var before = track.Segments.TakeWhile(part => !ReferenceEquals(part, section.GeometryParts[0])).ToArray();
        var after = before.Concat(reversed).Concat(track.Segments.Skip(before.Length + section.GeometryParts.Count)).ToArray();
        return BuildPlan(project, track, "反转轨迹段", after, NoMappings, NoRemovedSegments,
            true, false, token, ["拆分分段方向已反转；源时间未改写。"]);
    }

    public TrackEditPlan PrepareDeleteSplitSection(HikingProject project, Guid trackId,
        Guid sectionId, CancellationToken token = default)
        => PrepareDeleteSplitSections(project, trackId, new HashSet<Guid> { sectionId }, token);

    public TrackEditPlan PrepareDeleteSplitSections(HikingProject project, Guid trackId,
        IReadOnlySet<Guid> sectionIds, CancellationToken token = default)
    {
        var track = FindTrack(project, trackId);
        var sections = track.GetSplitSections();
        if (sectionIds.Count == 0 || sectionIds.Any(id => sections.All(section => section.Id != id)))
            throw new InvalidOperationException("所选拆分分段不存在。");
        if (sectionIds.Count == sections.Count) throw new InvalidOperationException("该操作将删除整条轨迹，请使用“删除轨迹”。");
        var removed = sections.Where(section => sectionIds.Contains(section.Id))
            .SelectMany(section => section.GeometryParts).Select(part => part.Id).ToHashSet();
        var after = track.Segments.Where(part => !removed.Contains(part.Id)).ToArray();
        return BuildPlan(project, track, "删除轨迹段", after, NoMappings, removed,
            false, true, token);
    }

    public TrackEditPlan PrepareMovePoint(HikingProject project, Guid trackId, Guid pointId,
        GeoCoordinate coordinate, CancellationToken cancellationToken = default)
    {
        coordinate.Validate();
        var (track, segment, segmentIndex, pointIndex) = FindPoint(project, trackId, pointId);
        var edited = CopySegment(segment);
        var original = segment.Points[pointIndex];
        edited.Points[pointIndex] = CopyPoint(original);
        edited.Points[pointIndex].Latitude = coordinate.Latitude;
        edited.Points[pointIndex].Longitude = coordinate.Longitude;
        edited.Points[pointIndex].IsCoordinateEdited = true;
        edited.Points[pointIndex].DemElevation = null;
        var after = track.Segments.ToArray();
        after[segmentIndex] = edited;
        return BuildPlan(project, track, "移动轨迹点", after, NoMappings, NoRemovedSegments, false, false, cancellationToken);
    }

    public TrackEditPlan PrepareInsertPoint(HikingProject project, Guid trackId, Guid segmentId,
        int afterPointIndex, GeoCoordinate coordinate, CancellationToken cancellationToken = default)
    {
        coordinate.Validate();
        var (track, segment, segmentIndex) = FindSegment(project, trackId, segmentId);
        if (afterPointIndex < 0 || afterPointIndex >= segment.Points.Count - 1)
            throw new ArgumentOutOfRangeException(nameof(afterPointIndex), "请选择轨迹段内的相邻两点。");
        var edited = CopySegment(segment);
        var a = edited.Points[afterPointIndex]; var b = edited.Points[afterPointIndex + 1];
        var inserted = NewPoint(coordinate);
        edited.Points.Insert(afterPointIndex + 1, inserted);
        var mapping = new Dictionary<TrackEdgeKey, IReadOnlyList<TrackEdgeKey>>
        {
            [new(a.Id, b.Id)] = [new(a.Id, inserted.Id), new(inserted.Id, b.Id)]
        };
        var after = track.Segments.ToArray(); after[segmentIndex] = edited;
        return BuildPlan(project, track, "添加轨迹点", after, mapping, NoRemovedSegments, false, false, cancellationToken);
    }

    public TrackEditPlan PrepareDeletePoint(HikingProject project, Guid trackId, Guid pointId,
        CancellationToken cancellationToken = default)
    {
        var (track, segment, segmentIndex, pointIndex) = FindPoint(project, trackId, pointId);
        if (segment.Points.Count <= 2)
            throw new InvalidOperationException("删除该点会使轨迹段失效。请明确选择“删除轨迹段”或取消。");
        var edited = CopySegment(segment);
        var mapping = new Dictionary<TrackEdgeKey, IReadOnlyList<TrackEdgeKey>>();
        if (pointIndex > 0 && pointIndex < segment.Points.Count - 1)
        {
            var a = segment.Points[pointIndex - 1].Id;
            var b = segment.Points[pointIndex].Id;
            var c = segment.Points[pointIndex + 1].Id;
            var shortcut = new TrackEdgeKey(a, c);
            mapping[new(a, b)] = [shortcut];
            mapping[new(b, c)] = [shortcut];
        }
        edited.Points.RemoveAt(pointIndex);
        var after = track.Segments.ToArray(); after[segmentIndex] = edited;
        return BuildPlan(project, track, "删除轨迹点", after, mapping, NoRemovedSegments, false, true, cancellationToken);
    }

    public TrackEditPlan PrepareReverseTrack(HikingProject project, Guid trackId,
        CancellationToken cancellationToken = default)
    {
        var track = FindTrack(project, trackId);
        var after = track.Segments.AsEnumerable().Reverse().Select(segment =>
        {
            var copy = CopySegment(segment);
            copy.Points.Reverse();
            return copy;
        }).ToArray();
        return BuildPlan(project, track, "反转整条轨迹", after, NoMappings, NoRemovedSegments, true, false, cancellationToken,
            ["轨迹方向已反转。起点/终点类型和源时间没有自动修改，请检查攻略节点类型和时间顺序。"]);
    }

    public TrackEditPlan PrepareReverseSegment(HikingProject project, Guid trackId, Guid segmentId,
        CancellationToken cancellationToken = default)
    {
        var (track, segment, index) = FindSegment(project, trackId, segmentId);
        var reversed = CopySegment(segment); reversed.Points.Reverse();
        var after = track.Segments.ToArray(); after[index] = reversed;
        return BuildPlan(project, track, "反转轨迹段", after, NoMappings, NoRemovedSegments, true, false, cancellationToken,
            ["轨迹段方向已反转；源时间未改写。"]);
    }

    public TrackEditPlan PrepareDeleteSegment(HikingProject project, Guid trackId, Guid segmentId,
        CancellationToken cancellationToken = default)
    {
        var (track, _, index) = FindSegment(project, trackId, segmentId);
        if (track.Segments.Count == 1)
            throw new InvalidOperationException("该操作将删除轨迹的全部几何。请使用“删除轨迹”。");
        var after = track.Segments.Where((_, i) => i != index).ToArray();
        return BuildPlan(project, track, "删除轨迹段", after, NoMappings, new HashSet<Guid> { segmentId }, false, true, cancellationToken);
    }

    public TrackEditPlan PrepareMergeAdjacentSegments(HikingProject project, Guid trackId,
        Guid firstSegmentId, CancellationToken cancellationToken = default)
    {
        RequireOrdinaryTrack(project, trackId);
        var (track, first, index) = FindSegment(project, trackId, firstSegmentId);
        if (index + 1 >= track.Segments.Count)
            throw new InvalidOperationException("请选择前后相邻的两个轨迹段。");
        var second = track.Segments[index + 1];
        var gap = GeoDistanceCalculator.Instance.CalculateDistance(
            first.EndPoint!.Coordinate, second.StartPoint!.Coordinate);
        if (!double.IsFinite(MergeToleranceMeters) || MergeToleranceMeters < 0)
            throw new InvalidOperationException("轨迹段合并容差无效。");
        if (gap > MergeToleranceMeters)
            throw new InvalidOperationException("两个轨迹段之间存在空间间隔。请使用“连接相邻分段”。");
        var merged = CopySegment(first);
        merged.Points.AddRange(second.Points.Skip(1));
        var mapping = new Dictionary<TrackEdgeKey, IReadOnlyList<TrackEdgeKey>>
        {
            [new(second.Points[0].Id, second.Points[1].Id)] =
                [new(first.EndPoint.Id, second.Points[1].Id)]
        };
        var after = track.Segments.Take(index).Concat([merged])
            .Concat(track.Segments.Skip(index + 2)).ToArray();
        return BuildPlan(project, track, "合并相邻分段", after, mapping,
            NoRemovedSegments, false, gap > 0, cancellationToken);
    }

    public TrackEditPlan PrepareConnectAdjacentSegments(HikingProject project, Guid trackId,
        Guid firstSegmentId, CancellationToken cancellationToken = default)
    {
        RequireOrdinaryTrack(project, trackId);
        var (track, first, index) = FindSegment(project, trackId, firstSegmentId);
        if (index + 1 >= track.Segments.Count)
            throw new InvalidOperationException("请选择前后相邻的两个轨迹段。");
        var second = track.Segments[index + 1];
        var from = first.EndPoint!.Coordinate;
        var to = second.StartPoint!.Coordinate;
        var length = GeoDistanceCalculator.Instance.CalculateDistance(from, to);
        var connector = new TrackSegment
        {
            Name = "人工直线连接",
            OriginalAttributes = new Dictionary<string, string?>
            { ["hg:editorProvenance"] = "人工直线连接" },
            Points = [NewPoint(from), NewPoint(to)]
        };
        var after = track.Segments.Take(index + 1).Concat([connector])
            .Concat(track.Segments.Skip(index + 1)).ToArray();
        return BuildPlan(project, track, "连接相邻分段", after, NoMappings,
            NoRemovedSegments, false, true, cancellationToken,
            [$"将在两端点间建立 {length:F1} 米直线连接，不进行道路或徒步路径规划。"]);
    }

    public TrackEditPlan PrepareSplitSegment(HikingProject project, Guid trackId, Guid segmentId,
        int startPointIndex, double fraction, CancellationToken cancellationToken = default)
    {
        RequireOrdinaryTrack(project, trackId);
        var (track, segment, index) = FindSegment(project, trackId, segmentId);
        if (startPointIndex < 0 || startPointIndex >= segment.Points.Count - 1 ||
            !double.IsFinite(fraction) || fraction <= 0 || fraction > 1)
            throw new ArgumentOutOfRangeException(nameof(fraction), "请选择轨迹段内部的拆分位置。");
        var a = segment.Points[startPointIndex]; var b = segment.Points[startPointIndex + 1];
        if (fraction == 1 && startPointIndex == segment.Points.Count - 2)
            throw new InvalidOperationException("不能在轨迹段末端拆分。");
        var coordinate = fraction == 1 ? b.Coordinate : new GeoCoordinate(
            a.Latitude + (b.Latitude - a.Latitude) * fraction,
            a.Longitude + (b.Longitude - a.Longitude) * fraction);
        var first = CopySegment(segment);
        first.Points = segment.Points.Take(startPointIndex + 1).ToList();
        var second = new TrackSegment { Name = segment.Name,
            SplitSectionDescription = segment.SplitSectionDescription,
            RouteProvenance = segment.RouteProvenance,
            OriginalAttributes = new Dictionary<string, string?>(segment.OriginalAttributes) };
        var mapping = new Dictionary<TrackEdgeKey, IReadOnlyList<TrackEdgeKey>>();
        if (fraction == 1)
        {
            first.Points.Add(b);
            var duplicate = CopyPoint(b); duplicate.Id = Guid.NewGuid();
            duplicate.IsInsertedByEditor = true;
            duplicate.IsCoordinateEdited = true;
            duplicate.SourceElevation = null; duplicate.Timestamp = null; duplicate.DemElevation = null;
            second.Points.Add(duplicate);
            second.Points.AddRange(segment.Points.Skip(startPointIndex + 2));
            mapping[new(b.Id, segment.Points[startPointIndex + 2].Id)] =
                [new(duplicate.Id, segment.Points[startPointIndex + 2].Id)];
        }
        else
        {
            var left = NewPoint(coordinate); var right = NewPoint(coordinate);
            first.Points.Add(left);
            second.Points.Add(right);
            second.Points.AddRange(segment.Points.Skip(startPointIndex + 1));
            mapping[new(a.Id, b.Id)] = [new(a.Id, left.Id), new(right.Id, b.Id)];
        }
        if (first.Points.Count < 2 || second.Points.Count < 2)
            throw new InvalidOperationException("拆分后轨迹段至少需要两个点。");
        var after = track.Segments.Take(index).Concat([first, second])
            .Concat(track.Segments.Skip(index + 1)).ToArray();
        var previousSectionId = segment.SplitSectionId ?? segment.Id;
        var nextSectionId = Guid.NewGuid();
        first.SplitSectionId = previousSectionId;
        second.SplitSectionId = nextSectionId;
        for (var following = index + 2; following < after.Length; following++)
        {
            if ((after[following].SplitSectionId ?? after[following].Id) != previousSectionId) break;
            after[following] = CopySegment(after[following]);
            after[following].SplitSectionId = nextSectionId;
        }
        var naming=TrackSplitNaming.Prepare(track,previousSectionId);
        for(var partIndex=0;partIndex<after.Length;partIndex++)
        {
            var id=after[partIndex].SplitSectionId??after[partIndex].Id;
            if(id!=previousSectionId&&id!=nextSectionId)continue;
            after[partIndex]=CopySegment(after[partIndex]);
            TrackSplitNaming.NamePart(track,after[partIndex],naming,id==nextSectionId);
        }
        return BuildPlan(project, track, "拆分轨迹段", after, mapping, NoRemovedSegments, false, false, cancellationToken,splitNaming:naming.Sequence);
    }

    public TrackEditPlan PrepareDeleteRange(HikingProject project, TrackRange range,
        CancellationToken cancellationToken = default) => PrepareRange(project, range, false, cancellationToken);

    public TrackEditPlan PrepareCropRange(HikingProject project, TrackRange range,
        CancellationToken cancellationToken = default) => PrepareRange(project, range, true, cancellationToken);

    private TrackEditPlan PrepareRange(HikingProject project, TrackRange range, bool crop,
        CancellationToken cancellationToken)
    {
        var track = FindTrack(project, range.TrackId);
        var start = range.Start.DistanceFromTrackStart <= range.End.DistanceFromTrackStart
            ? range.Start : range.End;
        var end = ReferenceEquals(start, range.Start) ? range.End : range.Start;
        var fingerprint = _fingerprints.GetGeometryFingerprint(track);
        ValidateRangeEndpoint(track, start, fingerprint);
        ValidateRangeEndpoint(track, end, fingerprint);
        if (end.DistanceFromTrackStart - start.DistanceFromTrackStart <= 1e-6)
            throw new InvalidOperationException("请选择长度大于零的轨迹区间。");
        var segments = new List<TrackSegment>();
        var changedMappings = new Dictionary<TrackEdgeKey, List<TrackEdgeKey>>();
        for (var s = 0; s < track.Segments.Count; s++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = track.Segments[s];
            var fullStart = new EdgeBoundary(0, 0);
            var fullEnd = new EdgeBoundary(source.Points.Count - 2, 1);
            if (crop)
            {
                if (s < start.SegmentIndex || s > end.SegmentIndex) continue;
                var from = s == start.SegmentIndex ? Boundary(start) : fullStart;
                var to = s == end.SegmentIndex ? Boundary(end) : fullEnd;
                var piece = Piece(source, from, to, false);
                if (piece is null) continue;
                segments.Add(piece);
                if (s == start.SegmentIndex) AddBoundaryMapping(source, from, piece, false, changedMappings);
                if (s == end.SegmentIndex) AddBoundaryMapping(source, to, piece, true, changedMappings);
            }
            else if (s < start.SegmentIndex || s > end.SegmentIndex) segments.Add(source);
            else
            {
                if (s == start.SegmentIndex)
                {
                    var boundary = Boundary(start);
                    var prefix = Piece(source, fullStart, boundary, false);
                    if (prefix is not null)
                    {
                        segments.Add(prefix);
                        AddBoundaryMapping(source, boundary, prefix, true, changedMappings);
                    }
                }
                if (s == end.SegmentIndex)
                {
                    var boundary = Boundary(end);
                    var suffix = Piece(source, boundary, fullEnd, s == start.SegmentIndex);
                    if (suffix is not null)
                    {
                        segments.Add(suffix);
                        AddBoundaryMapping(source, boundary, suffix, false, changedMappings);
                    }
                }
            }
        }
        if (segments.Count == 0)
            throw new InvalidOperationException("此操作将删除轨迹的全部几何。请使用“删除轨迹”。");
        var map = changedMappings.ToDictionary(item => item.Key,
            item => (IReadOnlyList<TrackEdgeKey>)item.Value.Distinct().ToArray());
        var removed = crop
            ? new[] { new TrackDistanceRange(0, start.DistanceFromTrackStart),
                new TrackDistanceRange(end.DistanceFromTrackStart, track.GetTotalLength()) }
            : [new TrackDistanceRange(start.DistanceFromTrackStart, end.DistanceFromTrackStart)];
        return BuildPlan(project, track, crop ? "保留所选区间" : "删除所选区间", segments,
            map, NoRemovedSegments, false, true, cancellationToken, removedOldRanges: removed);
    }

    private static void ValidateRangeEndpoint(Track track, TrackPosition position, string fingerprint)
    {
        if (position.TrackId != track.Id || position.TrackGeometryFingerprint != fingerprint ||
            position.SegmentIndex < 0 || position.SegmentIndex >= track.Segments.Count ||
            track.Segments[position.SegmentIndex].Id != position.TrackSegmentId ||
            position.StartPointIndex < 0 || position.EndPointIndex != position.StartPointIndex + 1 ||
            position.EndPointIndex >= track.Segments[position.SegmentIndex].Points.Count ||
            !double.IsFinite(position.Fraction) || position.Fraction is < 0 or > 1)
            throw new InvalidOperationException("所选区间位置已失效，请重新选择。");
    }

    private static EdgeBoundary Boundary(TrackPosition position) =>
        new(position.StartPointIndex, position.Fraction);

    private static TrackSegment? Piece(TrackSegment source, EdgeBoundary from, EdgeBoundary to, bool newId)
    {
        if (from.EdgeIndex > to.EdgeIndex ||
            from.EdgeIndex == to.EdgeIndex && from.Fraction >= to.Fraction) return null;
        var piece = CopySegment(source);
        if (newId) piece.Id = Guid.NewGuid();
        piece.Points = [];
        AddDistinct(piece.Points, BoundaryPoint(source, from));
        for (var i = from.EdgeIndex + 1; i <= to.EdgeIndex; i++)
            AddDistinct(piece.Points, source.Points[i]);
        AddDistinct(piece.Points, BoundaryPoint(source, to));
        return piece.Points.Count < 2 ? null : piece;
    }

    private static TrackPoint BoundaryPoint(TrackSegment source, EdgeBoundary boundary)
    {
        var a = source.Points[boundary.EdgeIndex];
        var b = source.Points[boundary.EdgeIndex + 1];
        if (boundary.Fraction == 0) return a;
        if (boundary.Fraction == 1) return b;
        return NewPoint(new GeoCoordinate(
            a.Latitude + (b.Latitude - a.Latitude) * boundary.Fraction,
            a.Longitude + (b.Longitude - a.Longitude) * boundary.Fraction));
    }

    private static void AddDistinct(List<TrackPoint> points, TrackPoint point)
    {
        if (points.Count == 0 || points[^1].Id != point.Id)
            points.Add(point);
    }

    private static void AddBoundaryMapping(TrackSegment source, EdgeBoundary boundary,
        TrackSegment piece, bool boundaryAtEnd,
        Dictionary<TrackEdgeKey, List<TrackEdgeKey>> mappings)
    {
        if (boundary.Fraction is 0 or 1) return;
        var old = new TrackEdgeKey(source.Points[boundary.EdgeIndex].Id,
            source.Points[boundary.EdgeIndex + 1].Id);
        var count = piece.Points.Count;
        var replacement = boundaryAtEnd
            ? new TrackEdgeKey(piece.Points[count - 2].Id, piece.Points[count - 1].Id)
            : new TrackEdgeKey(piece.Points[0].Id, piece.Points[1].Id);
        if (!mappings.TryGetValue(old, out var list)) mappings[old] = list = [];
        list.Add(replacement);
    }

    private readonly record struct EdgeBoundary(int EdgeIndex, double Fraction);

    private TrackEditPlan BuildPlan(HikingProject project, Track track, string operation,
        IReadOnlyList<TrackSegment> afterSegments,
        IReadOnlyDictionary<TrackEdgeKey, IReadOnlyList<TrackEdgeKey>> edgeMappings,
        IReadOnlySet<Guid> removedSegments, bool orderChanged, bool topologyChanged,
        CancellationToken cancellationToken, IReadOnlyList<string>? warnings = null,
        IReadOnlyList<TrackDistanceRange>? removedOldRanges = null,
        SplitSectionSequenceChange? splitNaming = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (afterSegments.Count == 0 || afterSegments.Any(segment => segment.Points.Count < 2))
            throw new InvalidOperationException("编辑结果包含无效轨迹段，项目未发生改变。");
        foreach (var point in afterSegments.SelectMany(segment => segment.Points)) point.Coordinate.Validate();
        var beforeFingerprint = _fingerprints.GetGeometryFingerprint(track);
        var candidate = new Track { Id = track.Id, Name = track.Name, Segments = afterSegments.ToList() };
        var afterFingerprint = _fingerprints.GetGeometryFingerprint(candidate);
        if (beforeFingerprint == afterFingerprint &&
            track.Segments.Select(s => (s.Id, s.SplitSectionId, s.SplitSectionName, s.SplitSectionDescription))
                .SequenceEqual(afterSegments.Select(s => (s.Id, s.SplitSectionId, s.SplitSectionName, s.SplitSectionDescription))))
            throw new InvalidOperationException("编辑后的轨迹几何与原几何相同。");
        var change = new TrackGeometryChangeSet(track.Id, beforeFingerprint, afterFingerprint,
            operation, edgeMappings, removedSegments, removedOldRanges ?? [], orderChanged, topologyChanged);
        var oldStates = project.GuideNodes.Where(node => node.TrackId == track.Id)
            .ToDictionary(node => node.Id, GuideNodeGeometryState.From);
        var newStates = project.MainGuideTrackId == track.Id
            ? _reconciler.Prepare(track, candidate, project.GuideNodes, change)
            : oldStates;
        cancellationToken.ThrowIfCancellationRequested();
        var oldActive = project.GuideSegments.ToArray();
        var oldRetired = project.RetiredGuideSegments.ToArray();
        IReadOnlyList<GuideSegment> newActive = oldActive;
        IReadOnlyList<GuideSegment> newRetired = oldRetired;
        if (project.MainGuideTrackId == track.Id)
        {
            var scratch = new HikingProject();
            scratch.AddTrack(candidate, false);
            scratch.MainGuideTrackId = candidate.Id;
            foreach (var node in project.GuideNodes)
            {
                var copy = new GuideNode { Id = node.Id, TrackId = node.TrackId,
                    StableOrder = node.StableOrder, IsOrphaned = node.IsOrphaned,
                    ExactPosition = node.ExactPosition };
                if (newStates.TryGetValue(node.Id, out var state)) state.Apply(copy);
                scratch.GuideNodes.Add(copy);
            }
            foreach (var segment in oldActive) scratch.GuideSegments.Add(BatchWaypointGuideConversion.CloneSegment(segment));
            foreach (var segment in oldRetired) scratch.RetiredGuideSegments.Add(BatchWaypointGuideConversion.CloneSegment(segment));
            var rebuilt = _guides.RebuildSegments(scratch);
            GuideStructureService.ReplaceSegments(scratch, rebuilt);
            newActive = scratch.GuideSegments.ToArray();
            newRetired = scratch.RetiredGuideSegments.ToArray();
        }
        var impact = new TrackEditImpact(newStates.Count(pair => !Equals(pair.Value, oldStates[pair.Key])),
            newStates.Count(pair => pair.Value.IsOrphaned && !oldStates[pair.Key].IsOrphaned),
            oldActive.Length, newActive.Count, track.GetTotalLength(), candidate.GetTotalLength());
        return new TrackEditPlan(project, track, track.GeometryRevision, track.Segments.ToArray(),
            afterSegments.ToArray(), change, oldStates, newStates, oldActive, oldRetired,
            newActive, newRetired, project.IsAnalysisResultStale, impact, warnings ?? [],splitNaming);
    }

    private static (Track Track, TrackSegment Segment, int SegmentIndex, int PointIndex)
        FindPoint(HikingProject project, Guid trackId, Guid pointId)
    {
        var track = FindTrack(project, trackId);
        for (var s = 0; s < track.Segments.Count; s++)
            for (var p = 0; p < track.Segments[s].Points.Count; p++)
                if (track.Segments[s].Points[p].Id == pointId)
                    return (track, track.Segments[s], s, p);
        throw new InvalidOperationException("所选轨迹点不属于当前轨迹。");
    }

    private static (Track Track, TrackSegment Segment, int SegmentIndex)
        FindSegment(HikingProject project, Guid trackId, Guid segmentId)
    {
        var track = FindTrack(project, trackId);
        for (var s = 0; s < track.Segments.Count; s++)
            if (track.Segments[s].Id == segmentId) return (track, track.Segments[s], s);
        throw new InvalidOperationException("所选轨迹段不属于当前轨迹。");
    }

    private static Track FindTrack(HikingProject project, Guid id) =>
        project.Tracks.FirstOrDefault(track => track.Id == id) ??
        throw new InvalidOperationException("所选轨迹不属于当前项目。");

    private static TrackSegment CopySegment(TrackSegment segment) => new()
    {
        Id = segment.Id, Name = segment.Name, SplitSectionId = segment.SplitSectionId,
        SplitSectionName = segment.SplitSectionName,
        SplitSectionDescription = segment.SplitSectionDescription, Points = segment.Points.ToList(),
        RouteProvenance = segment.RouteProvenance,
        OriginalAttributes = new Dictionary<string, string?>(segment.OriginalAttributes)
    };

    private static TrackPoint CopyPoint(TrackPoint point) => new()
    {
        Id = point.Id, Latitude = point.Latitude, Longitude = point.Longitude,
        SourceElevation = point.SourceElevation, DemElevation = point.DemElevation,
        Timestamp = point.Timestamp, DistanceFromStart = point.DistanceFromStart,
        IsInsertedByEditor = point.IsInsertedByEditor,
        IsCoordinateEdited = point.IsCoordinateEdited,
        OriginalAttributes = new Dictionary<string, string?>(point.OriginalAttributes)
    };

    private static TrackPoint NewPoint(GeoCoordinate coordinate) => new()
    {
        Latitude = coordinate.Latitude, Longitude = coordinate.Longitude,
        IsInsertedByEditor = true, IsCoordinateEdited = true
    };
}
