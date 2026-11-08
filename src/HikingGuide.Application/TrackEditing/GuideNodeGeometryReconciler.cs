using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;

namespace HikingGuide.Application.TrackEditing;

/// <summary>按稳定点 ID 映射原边；局部变化只查询明确映射的新边。</summary>
public sealed class GuideNodeGeometryReconciler
{
    private readonly IGeoDistanceService _distance = GeoDistanceCalculator.Instance;

    public IReadOnlyDictionary<Guid, GuideNodeGeometryState> Prepare(
        Track before, Track after, IEnumerable<GuideNode> nodes, TrackGeometryChangeSet changes)
    {
        var edges = IndexEdges(after);
        var result = new Dictionary<Guid, GuideNodeGeometryState>();
        foreach (var node in nodes.Where(node => node.TrackId == before.Id))
        {
            if (node.IsOrphaned || node.ExactPosition is not { } old ||
                old.SegmentIndex < 0 || old.SegmentIndex >= before.Segments.Count)
            {
                result[node.Id] = GuideNodeGeometryState.From(node);
                continue;
            }
            var oldPoints = before.Segments[old.SegmentIndex].Points;
            if (old.StartPointIndex < 0 || old.EndPointIndex >= oldPoints.Count)
            {
                result[node.Id] = GuideNodeGeometryState.Orphaned();
                continue;
            }
            var key = new TrackEdgeKey(oldPoints[old.StartPointIndex].Id, oldPoints[old.EndPointIndex].Id);
            if (changes.RemovedSegmentIds.Contains(before.Segments[old.SegmentIndex].Id))
            {
                result[node.Id] = GuideNodeGeometryState.Orphaned();
                continue;
            }
            if (changes.RemovedOldRanges.Any(range =>
                    old.DistanceFromTrackStart > range.StartMeters + 1e-6 &&
                    old.DistanceFromTrackStart < range.EndMeters - 1e-6))
            {
                result[node.Id] = GuideNodeGeometryState.Orphaned();
                continue;
            }
            var candidates = new List<(Edge Edge, bool Reversed)>();
            if (edges.TryGetValue(key, out var direct)) candidates.Add((direct, false));
            var reversedKey = new TrackEdgeKey(key.EndPointId, key.StartPointId);
            if (edges.TryGetValue(reversedKey, out var reversed)) candidates.Add((reversed, true));
            if (candidates.Count == 0 && changes.ChangedEdgeMappings.TryGetValue(key, out var replacements))
                foreach (var replacement in replacements)
                    if (edges.TryGetValue(replacement, out var edge)) candidates.Add((edge, false));
            if (candidates.Count == 0)
            {
                result[node.Id] = GuideNodeGeometryState.Orphaned();
                continue;
            }
            var unchanged = candidates.Count == 1 &&
                candidates[0].Edge.A == oldPoints[old.StartPointIndex].Coordinate &&
                candidates[0].Edge.B == oldPoints[old.EndPointIndex].Coordinate && !candidates[0].Reversed;
            var unchangedReversed = candidates.Count == 1 && candidates[0].Reversed &&
                candidates[0].Edge.A == oldPoints[old.EndPointIndex].Coordinate &&
                candidates[0].Edge.B == oldPoints[old.StartPointIndex].Coordinate;
            // 拆分或在原边上插点不改变线形。原标注可能离轨迹很远，
            // 这时必须以旧的精确吸附坐标选择替代边，避免在拆分点跳到另一半。
            var oldSnapStillOnReplacement = candidates.Any(candidate =>
            {
                var fraction = ProjectFraction(old.SnappedCoordinate,
                    candidate.Edge.A, candidate.Edge.B);
                return _distance.CalculateDistance(old.SnappedCoordinate,
                    Interpolate(candidate.Edge.A, candidate.Edge.B, fraction)) <= 0.01;
            });
            var projectionTarget = oldSnapStillOnReplacement
                ? old.SnappedCoordinate : node.OriginalCoordinate;
            var selected = candidates.Select(candidate =>
            {
                var fraction = unchanged ? old.Fraction : unchangedReversed ? 1 - old.Fraction :
                    ProjectFraction(projectionTarget, candidate.Edge.A, candidate.Edge.B);
                var snapped = Interpolate(candidate.Edge.A, candidate.Edge.B, fraction);
                return (candidate.Edge, Fraction: fraction, Snapped: snapped,
                    SelectionDistance: _distance.CalculateDistance(projectionTarget, snapped),
                    Separation: _distance.CalculateDistance(node.OriginalCoordinate, snapped));
            }).OrderBy(item => item.SelectionDistance).First();
            var position = new TrackPosition
            {
                TrackId = after.Id, TrackSegmentId = selected.Edge.SegmentId,
                SegmentIndex = selected.Edge.SegmentIndex,
                StartPointIndex = selected.Edge.StartIndex, EndPointIndex = selected.Edge.StartIndex + 1,
                Fraction = selected.Fraction,
                SnappedCoordinate = selected.Snapped,
                DistanceToTrack = selected.Separation,
                DistanceFromTrackStart = selected.Edge.TrackStart + selected.Edge.Length * selected.Fraction,
                DistanceFromSegmentStart = selected.Edge.SegmentStart + selected.Edge.Length * selected.Fraction,
                TrackGeometryFingerprint = changes.NewFingerprint
            };
            result[node.Id] = GuideNodeGeometryState.From(position);
        }
        return result;
    }

    private Dictionary<TrackEdgeKey, Edge> IndexEdges(Track track)
    {
        var edges = new Dictionary<TrackEdgeKey, Edge>();
        var cumulative = 0d;
        for (var s = 0; s < track.Segments.Count; s++)
        {
            var segment = track.Segments[s];
            var within = 0d;
            for (var i = 1; i < segment.Points.Count; i++)
            {
                var a = segment.Points[i - 1]; var b = segment.Points[i];
                var length = _distance.CalculateDistance(a.Coordinate, b.Coordinate);
                edges[new TrackEdgeKey(a.Id, b.Id)] = new Edge(segment.Id, s, i - 1,
                    a.Coordinate, b.Coordinate, cumulative + within, within, length);
                within += length;
            }
            cumulative += within;
        }
        return edges;
    }

    private static GeoCoordinate Interpolate(GeoCoordinate a, GeoCoordinate b, double fraction) =>
        new(a.Latitude + (b.Latitude - a.Latitude) * fraction,
            a.Longitude + (b.Longitude - a.Longitude) * fraction);

    private static double ProjectFraction(GeoCoordinate point, GeoCoordinate a, GeoCoordinate b)
    {
        var cosine = Math.Max(1e-8, Math.Cos(point.Latitude * Math.PI / 180));
        var ax = (a.Longitude - point.Longitude) * cosine;
        var ay = a.Latitude - point.Latitude;
        var dx = (b.Longitude - a.Longitude) * cosine;
        var dy = b.Latitude - a.Latitude;
        var denominator = dx * dx + dy * dy;
        return denominator == 0 ? 0 : Math.Clamp(-(ax * dx + ay * dy) / denominator, 0, 1);
    }

    private sealed record Edge(Guid SegmentId, int SegmentIndex, int StartIndex,
        GeoCoordinate A, GeoCoordinate B, double TrackStart, double SegmentStart, double Length);
}

public sealed record GuideNodeGeometryState(TrackPosition? Position, bool IsOrphaned)
{
    public static GuideNodeGeometryState From(GuideNode node) => new(node.ExactPosition, node.IsOrphaned);
    public static GuideNodeGeometryState From(TrackPosition position) => new(position, false);
    public static GuideNodeGeometryState Orphaned() => new(null, true);

    public void Apply(GuideNode node)
    {
        node.IsOrphaned = IsOrphaned;
        node.ExactPosition = Position;
        node.SnappedCoordinate = Position?.SnappedCoordinate;
        node.DistanceFromStart = Position?.DistanceFromTrackStart;
        node.DistanceToTrack = Position?.DistanceToTrack;
        node.TrackSegmentIndex = Position?.SegmentIndex;
        node.TrackPosition = Position?.Fraction;
        node.TrackGeometryFingerprint = Position?.TrackGeometryFingerprint;
    }
}
