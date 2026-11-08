using HikingGuide.Core.Models;

namespace HikingGuide.Application.TrackEditing;

/// <summary>记录旧边如何进入新几何；缺少映射的旧边只能失联，不能全轨迹重吸附。</summary>
public sealed record TrackGeometryChangeSet(
    Guid TrackId,
    string OldFingerprint,
    string NewFingerprint,
    string Operation,
    IReadOnlyDictionary<TrackEdgeKey, IReadOnlyList<TrackEdgeKey>> ChangedEdgeMappings,
    IReadOnlySet<Guid> RemovedSegmentIds,
    IReadOnlyList<TrackDistanceRange> RemovedOldRanges,
    bool OrderChanged,
    bool TopologyChanged);

public readonly record struct TrackEdgeKey(Guid StartPointId, Guid EndPointId);

public readonly record struct TrackDistanceRange(double StartMeters, double EndMeters);

public sealed record TrackRange(Guid TrackId, TrackPosition Start, TrackPosition End)
{
    public double LengthMeters => End.DistanceFromTrackStart - Start.DistanceFromTrackStart;
}

public sealed record TrackEditImpact(int AffectedGuideNodes, int OrphanedGuideNodes,
    int GuideSegmentsBefore, int GuideSegmentsAfter, double DistanceBefore, double DistanceAfter);
