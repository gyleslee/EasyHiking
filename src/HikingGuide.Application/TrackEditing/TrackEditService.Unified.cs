using HikingGuide.Core.Models;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Services;

namespace HikingGuide.Application.TrackEditing;

public sealed partial class TrackEditService
{
    public TrackEditPlan PrepareDeletePoints(HikingProject project, Guid trackId,
        IReadOnlySet<Guid> pointIds, CancellationToken token = default)
    {
        var track = FindTrack(project, trackId);
        if (pointIds.Count == 0 || pointIds.Any(id => !track.Segments.Any(s => s.Points.Any(p => p.Id == id))))
            throw new InvalidOperationException("所选轨迹点已失效，请重新选择。");
        var after = track.Segments.ToArray();
        var mappings = new Dictionary<TrackEdgeKey, IReadOnlyList<TrackEdgeKey>>();
        for (var s = 0; s < after.Length; s++)
        {
            var source = track.Segments[s];
            if (!source.Points.Any(p => pointIds.Contains(p.Id))) continue;
            var keep = Enumerable.Range(0, source.Points.Count).Where(i => !pointIds.Contains(source.Points[i].Id)).ToArray();
            if (keep.Length < 2)
                throw new InvalidOperationException("删除所选点后，有分段将不足两个点。请减少选中的点；如需删除该分段，请使用“删除轨迹”。本次未修改轨迹。");
            var copy = CopySegment(source);
            copy.Points = keep.Select(i => source.Points[i]).ToList();
            after[s] = copy;
            for (var i = 1; i < keep.Length; i++)
                if (keep[i] > keep[i - 1] + 1)
                    for (var old = keep[i - 1]; old < keep[i]; old++)
                        mappings[new(source.Points[old].Id, source.Points[old + 1].Id)] =
                            [new(source.Points[keep[i - 1]].Id, source.Points[keep[i]].Id)];
        }
        return BuildPlan(project, track, "删除轨迹点", after, mappings, NoRemovedSegments, false, true, token);
    }

    public TrackEditPlan PrepareReverseSections(HikingProject project, Guid trackId,
        IReadOnlySet<Guid> sectionIds, CancellationToken token = default)
    {
        var track = FindTrack(project, trackId);
        var sections = track.GetSplitSections();
        if (sectionIds.Count == 0 || sectionIds.Any(id => sections.All(s => s.Id != id)))
            throw new InvalidOperationException("所选分段已失效，请重新选择。");
        var after = track.Segments.ToArray();
        foreach (var section in sections.Where(s => sectionIds.Contains(s.Id)))
        {
            var indices = Enumerable.Range(0, after.Length).Where(i => section.GeometryParts.Any(p => p.Id == after[i].Id)).ToArray();
            var reversed = section.GeometryParts.Reverse().Select(p => { var c = CopySegment(p); c.Points.Reverse(); return c; }).ToArray();
            for (var i = 0; i < indices.Length; i++) after[indices[i]] = reversed[i];
        }
        return BuildPlan(project, track, "反转轨迹", after, NoMappings, NoRemovedSegments, true, false, token,
            ["所选轨迹分段方向已反转，未选分段保持原顺序。请检查节点类型及源时间顺序。"]);
    }

    public TrackEditPlan PrepareCutAndJoin(HikingProject project, TrackRange range,
        Guid? sectionId = null, CancellationToken token = default)
    {
        var track = FindTrack(project, range.TrackId);
        var (start, end) = OrderedRange(track, range);
        ValidateScope(track, sectionId, start, end);
        var first = track.Segments[start.SegmentIndex]; var last = track.Segments[end.SegmentIndex];
        var joined = CopySegment(first);
        joined.Points = Clip(first, new(0, 0), Boundary(start));
        var prefixCount = joined.Points.Count;
        foreach (var point in Clip(last, Boundary(end), new(last.Points.Count - 2, 1))) AddDistinct(joined.Points, point);
        if (joined.Points.Count < 2) throw new InvalidOperationException("裁剪后不足两个点，请重新选择区间。");
        var after = track.Segments.Take(start.SegmentIndex).Concat([joined]).Concat(track.Segments.Skip(end.SegmentIndex + 1)).ToArray();
        var replacements = Enumerable.Range(Math.Max(0, prefixCount - 2), Math.Min(joined.Points.Count - 1, prefixCount + 1) - Math.Max(0, prefixCount - 2))
            .Select(i => new TrackEdgeKey(joined.Points[i].Id, joined.Points[i + 1].Id)).ToArray();
        var mappings = new Dictionary<TrackEdgeKey, IReadOnlyList<TrackEdgeKey>>
        {
            [new(first.Points[start.StartPointIndex].Id, first.Points[start.EndPointIndex].Id)] = replacements,
            [new(last.Points[end.StartPointIndex].Id, last.Points[end.EndPointIndex].Id)] = replacements
        };
        MapBoundaryNeighbors(first, start, replacements, mappings);
        MapBoundaryNeighbors(last, end, replacements, mappings);
        return BuildPlan(project, track, "区间裁剪", after, mappings, NoRemovedSegments, false, true, token,
            ["区间内部的轨迹将删除，两个边界点以直线连接；直线不代表实际可通行道路。"],
            [new(start.DistanceFromTrackStart, end.DistanceFromTrackStart)]);
    }

    public TrackEditPlan PrepareTrimAtPoint(HikingProject project, TrackPosition position,
        bool deleteBefore, Guid? sectionId = null, CancellationToken token = default)
    {
        var track = FindTrack(project, position.TrackId);
        ValidateRangeEndpoint(track, position, _fingerprints.GetGeometryFingerprint(track));
        ValidateScope(track, sectionId, position, position);
        var scope = sectionId is Guid id ? track.GetSplitSections().Single(s => s.Id == id).GeometryParts.Select(p => p.Id).ToHashSet() : null;
        var after = new List<TrackSegment>();
        var mappings = new Dictionary<TrackEdgeKey, IReadOnlyList<TrackEdgeKey>>();
        for (var i = 0; i < track.Segments.Count; i++)
        {
            var source = track.Segments[i];
            if (scope is not null && !scope.Contains(source.Id)) { after.Add(source); continue; }
            if (i == position.SegmentIndex)
            {
                var copy = CopySegment(source);
                copy.Points = deleteBefore ? Clip(source, Boundary(position), new(source.Points.Count - 2, 1))
                    : Clip(source, new(0, 0), Boundary(position));
                if (copy.Points.Count < 2) throw new InvalidOperationException("保留此点后，当前分段将不足两个点。请重新选择裁剪位置，或使用“删除轨迹”删除整个对象。");
                after.Add(copy);
                var edge = deleteBefore ? 0 : copy.Points.Count - 2;
                mappings[new(source.Points[position.StartPointIndex].Id, source.Points[position.EndPointIndex].Id)] =
                    [new(copy.Points[edge].Id, copy.Points[edge + 1].Id)];
                MapBoundaryNeighbors(source, position, [new(copy.Points[edge].Id, copy.Points[edge + 1].Id)], mappings);
            }
            else if (deleteBefore ? i > position.SegmentIndex : i < position.SegmentIndex) after.Add(source);
        }
        if (after.Count == 0 || scope is not null && !after.Any(p => scope.Contains(p.Id)))
            throw new InvalidOperationException("该点位于保留方向的末端，裁剪后不足两个点。请重新选择，或使用“删除轨迹”删除整个对象。");
        var scopeParts = track.Segments.Where(p => scope is null || scope.Contains(p.Id)).ToArray();
        var lower = scopeParts[0].Points[0].DistanceFromStart;
        var upper = scopeParts[^1].Points[^1].DistanceFromStart;
        var removedParts = scopeParts.Where(p => after.All(a => a.Id != p.Id)).Select(p => p.Id).ToHashSet();
        return BuildPlan(project, track, deleteBefore ? "删除此点前轨迹" : "删除此点后轨迹", after,
            mappings, removedParts, false, true, token, removedOldRanges:
            [deleteBefore ? new(lower, position.DistanceFromTrackStart) : new(position.DistanceFromTrackStart, upper)]);
    }

    private (TrackPosition Start, TrackPosition End) OrderedRange(Track track, TrackRange range)
    {
        var fingerprint = _fingerprints.GetGeometryFingerprint(track);
        ValidateRangeEndpoint(track, range.Start, fingerprint); ValidateRangeEndpoint(track, range.End, fingerprint);
        var start = range.Start; var end = range.End;
        if (start.DistanceFromTrackStart > end.DistanceFromTrackStart) (start, end) = (end, start);
        if (end.DistanceFromTrackStart - start.DistanceFromTrackStart <= 1e-6) throw new InvalidOperationException("请选择两个不同的轨迹位置。");
        return (start, end);
    }
    private static void ValidateScope(Track track, Guid? sectionId, params TrackPosition[] positions)
    {
        if (sectionId is not Guid id) return;
        var section = track.GetSplitSections().SingleOrDefault(s => s.Id == id);
        if (section is null || positions.Any(p => section.GeometryParts.All(part => part.Id != p.TrackSegmentId)))
            throw new InvalidOperationException("请在同一个拆分分段内选择裁剪位置。");
    }
    private static List<TrackPoint> Clip(TrackSegment source, EdgeBoundary from, EdgeBoundary to)
    {
        var points = new List<TrackPoint>(); AddDistinct(points, BoundaryPoint(source, from));
        for (var i = from.EdgeIndex + 1; i <= to.EdgeIndex; i++) AddDistinct(points, source.Points[i]);
        AddDistinct(points, BoundaryPoint(source, to)); return points;
    }
    private static void MapBoundaryNeighbors(TrackSegment source, TrackPosition position,
        IReadOnlyList<TrackEdgeKey> replacements, Dictionary<TrackEdgeKey, IReadOnlyList<TrackEdgeKey>> mappings)
    {
        // 同一个GPS点可以被记录在前边的终端，也可以是后边的起端；两种身份都必须映射。
        if (position.Fraction == 0 && position.StartPointIndex > 0)
            mappings[new(source.Points[position.StartPointIndex - 1].Id, source.Points[position.StartPointIndex].Id)] = replacements;
        if (position.Fraction == 1 && position.EndPointIndex + 1 < source.Points.Count)
            mappings[new(source.Points[position.EndPointIndex].Id, source.Points[position.EndPointIndex + 1].Id)] = replacements;
    }
}
