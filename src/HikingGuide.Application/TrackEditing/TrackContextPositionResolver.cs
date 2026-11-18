using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;

namespace HikingGuide.Application.TrackEditing;

/// <summary>只在指定轨迹／分段内拾取右击位置，返回完整工作轨迹的稳定位置。</summary>
public sealed class TrackContextPositionResolver
{
    public TrackPosition Resolve(Track track, GeoCoordinate coordinate, Guid? sectionId = null, bool useGpsPoint = false)
    {
        var scope = sectionId is Guid id ? track.GetSplitSections().SingleOrDefault(s => s.Id == id)?.GeometryParts
            ?? throw new InvalidOperationException("该轨迹分段已失效，请重新右击选择。") : track.Segments;
        var snap = new TrackSnapService();
        var scopedTrack = new Track { Id = track.Id, Segments = scope.ToList() };
        var position = snap.Snap(scopedTrack, coordinate).Position ?? throw new InvalidOperationException("无法在该轨迹上确定位置，请重新选择。");
        var segmentIndex = track.Segments.FindIndex(p => p.Id == position.TrackSegmentId);
        var part = track.Segments[segmentIndex];
        var offset = track.Segments.Take(segmentIndex).Sum(p => p.GetLength());
        position = position with { SegmentIndex = segmentIndex, DistanceFromTrackStart = offset + position.DistanceFromSegmentStart,
            TrackGeometryFingerprint = snap.GetGeometryFingerprint(track) };
        if (!useGpsPoint) return position;
        var first = part.Points[position.StartPointIndex]; var second = part.Points[position.EndPointIndex];
        var index = GeoDistanceCalculator.Instance.CalculateDistance(coordinate, first.Coordinate) <=
            GeoDistanceCalculator.Instance.CalculateDistance(coordinate, second.Coordinate) ? position.StartPointIndex : position.EndPointIndex;
        var edge = Math.Min(index, part.Points.Count - 2);
        var point = part.Points[index];
        return position with { StartPointIndex = edge, EndPointIndex = edge + 1, Fraction = index == edge ? 0 : 1,
            SnappedCoordinate = point.Coordinate, DistanceToTrack = 0,
            DistanceFromSegmentStart = part.Points.Take(index).Zip(part.Points.Skip(1), (a,b) => GeoDistanceCalculator.Instance.CalculateDistance(a.Coordinate,b.Coordinate)).Sum(),
            DistanceFromTrackStart = offset + part.Points.Take(index).Zip(part.Points.Skip(1), (a,b) => GeoDistanceCalculator.Instance.CalculateDistance(a.Coordinate,b.Coordinate)).Sum() };
    }
}
