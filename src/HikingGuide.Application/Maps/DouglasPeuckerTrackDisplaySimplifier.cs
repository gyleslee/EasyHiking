using System.Collections.Concurrent;
using HikingGuide.Core.Models;

namespace HikingGuide.Application.Maps;

public sealed class DouglasPeuckerTrackDisplaySimplifier(MapDisplayProfile? profile = null) : ITrackDisplaySimplifier
{
    private readonly MapDisplayProfile _profile = profile ?? MapDisplayProfile.Default;
    private readonly ConcurrentDictionary<CacheKey, IReadOnlyList<IReadOnlyList<MapCoordinate>>> _cache = new();

    public MapTrackFeature Simplify(Track track, bool isActive, double zoom, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        var bucket = _profile.GetCacheZoomBucket(zoom);
        var key = new CacheKey(track.Id, track.GeometryRevision, bucket);
        var segments = _cache.GetOrAdd(key, _ => Build(track, zoom, cancellationToken));
        return new MapTrackFeature(
            track.Id,
            track.Name ?? "未命名轨迹",
            isActive,
            segments,
            track.GetPointCount(),
            segments.Sum(segment => segment.Count),
            track.DisplayColor);
    }

    public void Invalidate(Guid trackId)
    {
        foreach (var key in _cache.Keys.Where(key => key.TrackId == trackId)) _cache.TryRemove(key, out _);
    }

    private IReadOnlyList<IReadOnlyList<MapCoordinate>> Build(Track track, double zoom, CancellationToken cancellationToken)
    {
        var tolerance = _profile.GetToleranceMeters(zoom);
        var result = new List<IReadOnlyList<MapCoordinate>>(track.Segments.Count);
        foreach (var segment in track.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = segment.Points.Select(point => new MapCoordinate(point.Longitude, point.Latitude)).ToArray();
            result.Add(SimplifySegment(source, tolerance, cancellationToken));
        }
        return result;
    }

    public static IReadOnlyList<MapCoordinate> SimplifySegment(
        IReadOnlyList<MapCoordinate> points,
        double toleranceMeters,
        CancellationToken cancellationToken)
    {
        if (points.Count <= 2 || toleranceMeters <= 0) return points.ToArray();
        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int Start, int End)>();
        stack.Push((0, points.Count - 1));
        var toleranceSquared = toleranceMeters * toleranceMeters;

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (start, end) = stack.Pop();
            var maxDistance = 0d;
            var maxIndex = -1;
            for (var index = start + 1; index < end; index++)
            {
                var distance = PerpendicularDistanceSquared(points[index], points[start], points[end]);
                if (distance <= maxDistance) continue;
                maxDistance = distance;
                maxIndex = index;
            }
            if (maxIndex < 0 || maxDistance <= toleranceSquared) continue;
            keep[maxIndex] = true;
            stack.Push((start, maxIndex));
            stack.Push((maxIndex, end));
        }

        var simplified = new List<MapCoordinate>();
        for (var index = 0; index < points.Count; index++) if (keep[index]) simplified.Add(points[index]);
        return simplified;
    }

    private static double PerpendicularDistanceSquared(MapCoordinate point, MapCoordinate start, MapCoordinate end)
    {
        var referenceLatitude = (start.Latitude + end.Latitude + point.Latitude) / 3d * Math.PI / 180d;
        const double metersPerDegreeLatitude = 111_320d;
        var metersPerDegreeLongitude = metersPerDegreeLatitude * Math.Cos(referenceLatitude);
        var px = (point.Longitude - start.Longitude) * metersPerDegreeLongitude;
        var py = (point.Latitude - start.Latitude) * metersPerDegreeLatitude;
        var ex = (end.Longitude - start.Longitude) * metersPerDegreeLongitude;
        var ey = (end.Latitude - start.Latitude) * metersPerDegreeLatitude;
        var lengthSquared = ex * ex + ey * ey;
        if (lengthSquared <= double.Epsilon) return px * px + py * py;
        var t = Math.Clamp((px * ex + py * ey) / lengthSquared, 0, 1);
        var dx = px - t * ex;
        var dy = py - t * ey;
        return dx * dx + dy * dy;
    }

    private readonly record struct CacheKey(Guid TrackId, long GeometryRevision, int ZoomBucket);
}
