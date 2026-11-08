using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;

namespace HikingGuide.Application.Guides;

public sealed class GuideStructureService(TrackSnapService? snapService = null)
{
    private readonly TrackSnapService _snap = snapService ?? new TrackSnapService();

    public void ClearSnapCache() => _snap.Clear();

    public void SnapNode(GuideNode node, Track track)
    {
        ApplyPosition(node, SnapNodeToPosition(track, node.OriginalCoordinate, node.PreferredTrackPosition));
    }

    public TrackPosition SnapNodeToPosition(Track track, GeoCoordinate coordinate, TrackPosition? preferred = null)
    {
        if (preferred is not null && preferred.TrackId == track.Id &&
            preferred.TrackGeometryFingerprint == _snap.GetGeometryFingerprint(track) &&
            track.Segments.Any(s => s.Id == preferred.TrackSegmentId))
            return preferred with { DistanceToTrack = GeoDistanceCalculator.Instance.CalculateDistance(coordinate, preferred.SnappedCoordinate) };
        var result = _snap.Snap(track, coordinate);
        return result.Position ?? throw new InvalidOperationException(result.FailureReason);
    }

    public IReadOnlyList<TrackPosition> FindCandidatePositions(Track track, GeoCoordinate coordinate, int count = 5) =>
        _snap.SnapCandidates(track, coordinate, count).Where(result => result.Position is not null)
            .Select(result => result.Position!).ToArray();

    public void ApplyPosition(GuideNode node, TrackPosition position)
    {
        if (node.TrackId is not null && node.TrackId != position.TrackId)
            throw new ArgumentException("吸附结果不属于攻略节点指定的轨迹。", nameof(position));
        node.TrackId = position.TrackId;
        node.ExactPosition = position;
        node.SnappedCoordinate = position.SnappedCoordinate;
        node.DistanceFromStart = position.DistanceFromTrackStart;
        node.DistanceToTrack = position.DistanceToTrack;
        node.TrackGeometryFingerprint = position.TrackGeometryFingerprint;
        node.TrackSegmentIndex = position.SegmentIndex;
        node.TrackPosition = position.Fraction;
        node.IsOrphaned = false;
    }

    public void CheckPositions(HikingProject project)
    {
        var fingerprints = project.Tracks.ToDictionary(track => track.Id, _snap.GetGeometryFingerprint);
        var trackLengths = project.Tracks.ToDictionary(track => track.Id, track => track.GetTotalLength());
        foreach (var node in project.GuideNodes)
        {
            var track = project.Tracks.FirstOrDefault(t => t.Id == node.TrackId);
            var position = node.ExactPosition;
            if (track is null || position is null || position.TrackId != track.Id ||
                position.SegmentIndex < 0 || position.SegmentIndex >= track.Segments.Count ||
                track.Segments[position.SegmentIndex].Id != position.TrackSegmentId ||
                position.StartPointIndex < 0 ||
                position.EndPointIndex >= track.Segments[position.SegmentIndex].Points.Count ||
                position.EndPointIndex < position.StartPointIndex ||
                position.Fraction is < 0 or > 1 || !double.IsFinite(position.Fraction) ||
                !double.IsFinite(position.DistanceFromTrackStart) || position.DistanceFromTrackStart < 0 ||
                position.DistanceFromTrackStart > trackLengths[track.Id] + 0.1 ||
                !double.IsFinite(position.DistanceToTrack) || position.DistanceToTrack < 0 ||
                position.TrackGeometryFingerprint != fingerprints[track.Id])
                node.IsOrphaned = true;
            else node.IsOrphaned = false;
        }
    }

    public IReadOnlyList<GuideNode> SortNodes(IEnumerable<GuideNode> nodes, Guid trackId) => nodes
        .Where(node => node.TrackId == trackId && !node.IsOrphaned && node.ExactPosition is not null)
        .OrderBy(node => node.ExactPosition!.DistanceFromTrackStart)
        .ThenBy(node => node.StableOrder)
        .ThenBy(node => node.Id)
        .ToArray();

    public IReadOnlyList<GuideSegment> RebuildSegments(HikingProject project)
    {
        var old = project.GuideSegments.Concat(project.RetiredGuideSegments)
            .GroupBy(segment => (segment.StartNodeId, segment.EndNodeId))
            .ToDictionary(group => group.Key, group => group.First());
        var rebuilt = new List<GuideSegment>();
        foreach (var track in project.Tracks.Where(track => track.Id == project.MainGuideTrackId))
        {
            var ordered = project.GuideNodes.Where(node => node.TrackId == track.Id)
                .OrderBy(node => node.ExactPosition?.DistanceFromTrackStart ?? node.DistanceFromStart ?? double.PositiveInfinity)
                .ThenBy(node => node.StableOrder).ThenBy(node => node.Id).ToArray();
            for (var index = 1; index < ordered.Length; index++)
            {
                var start = ordered[index - 1];
                var end = ordered[index];
                if (start.IsOrphaned || end.IsOrphaned || start.ExactPosition is null || end.ExactPosition is null)
                    continue;
                if (!old.TryGetValue((start.Id, end.Id), out var segment)) segment = new GuideSegment
                {
                    StartNodeId = start.Id, EndNodeId = end.Id
                };
                segment.TrackId = track.Id;
                segment.StartDistance = start.ExactPosition!.DistanceFromTrackStart;
                segment.EndDistance = end.ExactPosition!.DistanceFromTrackStart;
                rebuilt.Add(segment);
            }
        }
        return rebuilt;
    }

    public static void ReplaceSegments(HikingProject project, IEnumerable<GuideSegment> activeSegments)
    {
        var next = activeSegments.ToArray();
        var nextIds = next.Select(segment => segment.Id).ToHashSet();
        foreach (var previous in project.GuideSegments.Where(segment => !nextIds.Contains(segment.Id)))
            if (project.RetiredGuideSegments.All(segment => segment.Id != previous.Id))
                project.RetiredGuideSegments.Add(previous);
        foreach (var active in next)
        {
            var retired = project.RetiredGuideSegments.FirstOrDefault(segment => segment.Id == active.Id);
            if (retired is not null) project.RetiredGuideSegments.Remove(retired);
        }
        project.GuideSegments.Clear();
        foreach (var segment in next) project.GuideSegments.Add(segment);
    }

    /// <summary>返回每个 TrackSegment 独立的原始几何切片，绝不跨段补线。</summary>
    public IReadOnlyList<IReadOnlyList<GeoCoordinate>> ExtractRange(Track track, TrackPosition start, TrackPosition end)
    {
        if (start.TrackId != track.Id || end.TrackId != track.Id ||
            start.DistanceFromTrackStart > end.DistanceFromTrackStart)
            throw new ArgumentException("攻略分段位置与轨迹不一致。");
        var pieces = new List<IReadOnlyList<GeoCoordinate>>();
        for (var s = start.SegmentIndex; s <= end.SegmentIndex; s++)
        {
            var source = track.Segments[s].Points;
            if (source.Count == 0) continue;
            var from = s == start.SegmentIndex ? start : null;
            var to = s == end.SegmentIndex ? end : null;
            var piece = new List<GeoCoordinate>();
            piece.Add(from?.SnappedCoordinate ?? source[0].Coordinate);
            var first = from is null ? 1 : from.EndPointIndex;
            var last = to is null ? source.Count - 1 : to.StartPointIndex;
            for (var p = first; p <= last; p++)
            {
                if (p >= 0 && p < source.Count && piece[^1] != source[p].Coordinate)
                    piece.Add(source[p].Coordinate);
            }
            var lastCoordinate = to?.SnappedCoordinate ?? source[^1].Coordinate;
            if (piece[^1] != lastCoordinate) piece.Add(lastCoordinate);
            pieces.Add(piece);
        }
        return pieces;
    }
}
