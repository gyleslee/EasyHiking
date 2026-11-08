using System.Security.Cryptography;
using System.Text;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;

namespace HikingGuide.Core.Services;

public sealed record TrackSnapResult(GeoCoordinate OriginalCoordinate, TrackPosition? Position, string? FailureReason = null)
{
    public bool IsSuccess => Position is not null;
}

/// <summary>完整原始线段空间索引；不使用地图简化数据，也不修改轨迹。</summary>
public sealed class TrackSnapService
{
    private const double EarthRadius = 6_371_008.8;
    private readonly IGeoDistanceService _distance;
    private readonly Dictionary<Guid, CachedIndex> _cache = [];

    public TrackSnapService(IGeoDistanceService? distance = null) => _distance = distance ?? GeoDistanceCalculator.Instance;

    public string GetGeometryFingerprint(Track track)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var segment in track.Segments)
        {
            hash.AppendData(segment.Id.ToByteArray());
            foreach (var point in segment.Points)
            {
                hash.AppendData(BitConverter.GetBytes(point.Latitude));
                hash.AppendData(BitConverter.GetBytes(point.Longitude));
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public TrackSnapResult Snap(Track track, GeoCoordinate coordinate) => SnapCandidates(track, coordinate, 1).First();

    public IReadOnlyList<TrackSnapResult> SnapCandidates(Track track, GeoCoordinate coordinate, int count)
    {
        ArgumentNullException.ThrowIfNull(track);
        coordinate.Validate();
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        var index = GetIndex(track);
        if (index.Parts.Length == 0) return [new TrackSnapResult(coordinate, null, "轨迹没有可吸附的线段。")];
        var candidates = new List<TrackPosition>(count);
        var queue = new PriorityQueue<IndexNode, double>();
        queue.Enqueue(index.Root!, LowerBound(index.Root!, coordinate));
        while (queue.TryDequeue(out var node, out var lowerBound))
        {
            if (candidates.Count == count && lowerBound > candidates[^1].DistanceToTrack + 1e-8) break;
            if (node.Parts is not null)
            {
                foreach (var part in node.Parts)
                {
                    var candidate = Project(track.Id, index.Fingerprint, part, coordinate);
                    candidates.Add(candidate);
                    candidates.Sort((a, b) =>
                    {
                        var compare = a.DistanceToTrack.CompareTo(b.DistanceToTrack);
                        return compare != 0 ? compare : a.DistanceFromTrackStart.CompareTo(b.DistanceFromTrackStart);
                    });
                    if (candidates.Count > count) candidates.RemoveAt(candidates.Count - 1);
                }
            }
            else
            {
                if (node.Left is not null) queue.Enqueue(node.Left, LowerBound(node.Left, coordinate));
                if (node.Right is not null) queue.Enqueue(node.Right, LowerBound(node.Right, coordinate));
            }
        }
        return candidates.Select(position => new TrackSnapResult(coordinate, position)).ToArray();
    }

    public void Release(Guid trackId) => _cache.Remove(trackId);

    /// <summary>获取容差内的全部原始线段投影，不限制返回数量；用于重复路径和折返的序列消歧。</summary>
    public IReadOnlyList<TrackPosition> SnapAllWithinTolerance(Track track, GeoCoordinate coordinate,
        double maximumDistanceMeters, double nearestExtraToleranceMeters)
    {
        ArgumentNullException.ThrowIfNull(track);
        coordinate.Validate();
        if (!double.IsFinite(maximumDistanceMeters) || maximumDistanceMeters < 0 ||
            !double.IsFinite(nearestExtraToleranceMeters) || nearestExtraToleranceMeters < 0)
            throw new ArgumentException("轨迹投影容差必须为有限的非负数。");
        var index = GetIndex(track);
        if (index.Root is null) return [];
        var candidates = new List<TrackPosition>();
        var queue = new Stack<IndexNode>();
        queue.Push(index.Root);
        while (queue.TryPop(out var node))
        {
            if (LowerBound(node, coordinate) > maximumDistanceMeters + 1e-8) continue;
            if (node.Parts is not null)
            {
                foreach (var part in node.Parts)
                {
                    var position = Project(track.Id, index.Fingerprint, part, coordinate);
                    if (position.DistanceToTrack <= maximumDistanceMeters) candidates.Add(position);
                }
            }
            else
            {
                if (node.Left is not null) queue.Push(node.Left);
                if (node.Right is not null) queue.Push(node.Right);
            }
        }
        if (candidates.Count == 0) return [];
        var nearest = candidates.Min(p => p.DistanceToTrack);
        return candidates.Where(p => p.DistanceToTrack <= nearest + nearestExtraToleranceMeters)
            .OrderBy(p => p.DistanceFromTrackStart).ThenBy(p => p.DistanceToTrack).ToArray();
    }

    public void Clear() => _cache.Clear();

    private CachedIndex GetIndex(Track track)
    {
        if (_cache.TryGetValue(track.Id, out var cached) && ReferenceEquals(cached.Track, track) &&
            cached.Revision == track.GeometryRevision) return cached;
        var fingerprint = GetGeometryFingerprint(track);
        var parts = new List<LinePart>();
        var trackStart = 0d;
        for (var s = 0; s < track.Segments.Count; s++)
        {
            var segment = track.Segments[s];
            var segmentStart = 0d;
            for (var p = 1; p < segment.Points.Count; p++)
            {
                var a = segment.Points[p - 1].Coordinate;
                var b = segment.Points[p].Coordinate;
                var length = _distance.CalculateDistance(a, b);
                parts.Add(new LinePart(segment.Id, s, p - 1, p, a, b, trackStart + segmentStart,
                    segmentStart, length));
                segmentStart += length;
            }
            if (segment.Points.Count == 1)
            {
                var a = segment.Points[0].Coordinate;
                parts.Add(new LinePart(segment.Id, s, 0, 0, a, a, trackStart, 0, 0));
            }
            trackStart += segmentStart;
        }
        var array = parts.ToArray();
        cached = new CachedIndex(track, track.GeometryRevision, fingerprint, array, BuildTree(array));
        _cache[track.Id] = cached;
        return cached;
    }

    private static TrackPosition Project(Guid trackId, string fingerprint, LinePart part, GeoCoordinate point)
    {
        var cos = Math.Max(1e-8, Math.Cos(point.Latitude * Math.PI / 180));
        var ax = (part.A.Longitude - point.Longitude) * Math.PI / 180 * EarthRadius * cos;
        var ay = (part.A.Latitude - point.Latitude) * Math.PI / 180 * EarthRadius;
        var bx = (part.B.Longitude - point.Longitude) * Math.PI / 180 * EarthRadius * cos;
        var by = (part.B.Latitude - point.Latitude) * Math.PI / 180 * EarthRadius;
        var dx = bx - ax;
        var dy = by - ay;
        var fraction = dx == 0 && dy == 0 ? 0 : Math.Clamp(-(ax * dx + ay * dy) / (dx * dx + dy * dy), 0, 1);
        var snapped = new GeoCoordinate(part.A.Latitude + (part.B.Latitude - part.A.Latitude) * fraction,
            part.A.Longitude + (part.B.Longitude - part.A.Longitude) * fraction);
        var distance = GeoDistanceCalculator.Instance.CalculateDistance(point, snapped);
        return new TrackPosition
        {
            TrackId = trackId, TrackSegmentId = part.SegmentId, SegmentIndex = part.SegmentIndex,
            StartPointIndex = part.StartIndex, EndPointIndex = part.EndIndex, Fraction = fraction,
            DistanceFromTrackStart = part.TrackStart + part.Length * fraction,
            DistanceFromSegmentStart = part.SegmentStart + part.Length * fraction,
            SnappedCoordinate = snapped, DistanceToTrack = distance, TrackGeometryFingerprint = fingerprint
        };
    }

    private static double LowerBound(IndexNode node, GeoCoordinate point)
    {
        // 球面上点到经纬度包络的严格下界，避免远距离查询时局部平面近似造成错误剪枝。
        var lonDelta = point.Longitude >= node.MinLon && point.Longitude <= node.MaxLon ? 0 :
            Math.Min(WrappedDifference(point.Longitude, node.MinLon), WrappedDifference(point.Longitude, node.MaxLon));
        var queryLat = point.Latitude * Math.PI / 180;
        var minLat = node.MinLat * Math.PI / 180;
        var maxLat = node.MaxLat * Math.PI / 180;
        var a = Math.Sin(queryLat);
        var b = Math.Cos(queryLat) * Math.Cos(lonDelta * Math.PI / 180);
        double Dot(double latitude) => a * Math.Sin(latitude) + b * Math.Cos(latitude);
        var maxDot = Math.Max(Dot(minLat), Dot(maxLat));
        var optimalLat = Math.Atan2(a, b);
        if (optimalLat >= minLat && optimalLat <= maxLat) maxDot = Math.Max(maxDot, Dot(optimalLat));
        return Math.Acos(Math.Clamp(maxDot, -1, 1)) * EarthRadius;
    }

    private static double WrappedDifference(double a, double b)
    {
        var difference = Math.Abs(a - b) % 360;
        return Math.Min(difference, 360 - difference);
    }

    private static IndexNode? BuildTree(LinePart[] parts)
    {
        if (parts.Length == 0) return null;
        var minLat = parts.Min(p => Math.Min(p.A.Latitude, p.B.Latitude));
        var maxLat = parts.Max(p => Math.Max(p.A.Latitude, p.B.Latitude));
        var minLon = parts.Min(p => Math.Min(p.A.Longitude, p.B.Longitude));
        var maxLon = parts.Max(p => Math.Max(p.A.Longitude, p.B.Longitude));
        if (parts.Length <= 24) return new IndexNode(minLat, maxLat, minLon, maxLon, parts, null, null);
        if (maxLon - minLon > maxLat - minLat)
            Array.Sort(parts, (a, b) => (a.A.Longitude + a.B.Longitude).CompareTo(b.A.Longitude + b.B.Longitude));
        else Array.Sort(parts, (a, b) => (a.A.Latitude + a.B.Latitude).CompareTo(b.A.Latitude + b.B.Latitude));
        var mid = parts.Length / 2;
        return new IndexNode(minLat, maxLat, minLon, maxLon, null,
            BuildTree(parts[..mid]), BuildTree(parts[mid..]));
    }

    private sealed record LinePart(Guid SegmentId, int SegmentIndex, int StartIndex, int EndIndex,
        GeoCoordinate A, GeoCoordinate B, double TrackStart, double SegmentStart, double Length);
    private sealed record IndexNode(double MinLat, double MaxLat, double MinLon, double MaxLon,
        LinePart[]? Parts, IndexNode? Left, IndexNode? Right);
    private sealed record CachedIndex(Track Track, long Revision, string Fingerprint, LinePart[] Parts, IndexNode? Root);
}
