using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;

namespace HikingGuide.Core.Elevation;

public sealed record ElevationGuideMarker(Guid NodeId, double DistanceMeters, double ElevationMeters, GeoCoordinate Coordinate);

/// <summary>攻略节点在当前高程来源曲线上的投影，不使用节点自带高程补洞，也不跨物理轨迹段插值。</summary>
public static class ElevationGuideMarkers
{
    public static IReadOnlyList<ElevationGuideMarker> Build(HikingProject project, ElevationTargetDescriptor target, ElevationAnalysisResult result)
    {
        if (project.Id != target.ProjectId || project.Id != result.ProjectId || target.TargetType != result.TargetType || target.TargetId != result.TargetId || target.TrackId != result.TrackId) return [];
        var track = project.Tracks.FirstOrDefault(t => t.Id == target.TrackId); if (track is null) return [];
        double offset = 0;
        if (target.TargetType == ElevationTargetType.GuideSegment)
        {
            var segment = project.GuideSegments.FirstOrDefault(s => s.Id == target.TargetId);
            var start = project.GuideNodes.FirstOrDefault(n => n.Id == segment?.StartNodeId);
            if (start?.ExactPosition is not { } position) return [];
            offset = position.DistanceFromTrackStart;
        }
        else if (target.TargetType == ElevationTargetType.SplitSection)
        {
            var first = target.GeometryParts.FirstOrDefault(); if (first is null) return [];
            var index = track.Segments.FindIndex(p => p.Id == first.Id); if (index < 0) return [];
            offset = track.Segments.Take(index).Sum(p => p.Length);
        }
        var partIndices = target.GeometryParts.Select((part, index) => (part.Id, index)).ToDictionary(p => p.Id, p => p.index);
        var series = result.Series.GroupBy(p => p.PartIndex).ToDictionary(g => g.Key, g => g.OrderBy(p => p.DistanceMeters).ToArray());
        var markers = new List<ElevationGuideMarker>();
        foreach (var node in project.GuideNodes)
        {
            if (node.TrackId != track.Id || node.IsOrphaned || node.ExactPosition is not { } position || position.TrackId != track.Id
                || !partIndices.TryGetValue(position.TrackSegmentId, out var part) || !series.TryGetValue(part, out var samples)) continue;
            var x = position.DistanceFromTrackStart - offset;
            if (x < -1e-6 || x > result.Summary.DistanceMeters + 1e-6) continue;
            var low = 0; var high = samples.Length;
            while (low < high) { var mid = (low + high) / 2; if (samples[mid].DistanceMeters < x) low = mid + 1; else high = mid; }
            double? y = null;
            if (low < samples.Length && Math.Abs(samples[low].DistanceMeters - x) < 1e-6) y = samples[low].ProcessedElevationMeters;
            else if (low > 0 && low < samples.Length && samples[low - 1].ProcessedElevationMeters is double a && samples[low].ProcessedElevationMeters is double b)
            {
                var from = samples[low - 1]; var to = samples[low]; var span = to.DistanceMeters - from.DistanceMeters;
                if (span > 0) y = a + (b - a) * (x - from.DistanceMeters) / span;
            }
            if (y is double height && double.IsFinite(height)) markers.Add(new(node.Id, x, height, position.SnappedCoordinate));
        }
        return markers.OrderBy(m => m.DistanceMeters).ToArray();
    }
}
