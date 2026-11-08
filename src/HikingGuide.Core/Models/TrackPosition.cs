using HikingGuide.Core.Geometry;

namespace HikingGuide.Core.Models;

/// <summary>在完整原始轨迹线段上的位置；距离单位均为米。</summary>
public sealed record TrackPosition
{
    public Guid TrackId { get; init; }
    public Guid TrackSegmentId { get; init; }
    public int SegmentIndex { get; init; }
    public int StartPointIndex { get; init; }
    public int EndPointIndex { get; init; }
    public double Fraction { get; init; }
    public double DistanceFromTrackStart { get; init; }
    public double DistanceFromSegmentStart { get; init; }
    public GeoCoordinate SnappedCoordinate { get; init; }
    public double DistanceToTrack { get; init; }
    public string? TrackGeometryFingerprint { get; init; }
}
