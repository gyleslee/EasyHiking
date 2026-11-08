using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;

namespace HikingGuide.Application.Maps;

public sealed record DirectionArrow(Guid TrackId, Guid SegmentId,
    GeoCoordinate Coordinate, double BearingDegrees, double DistanceFromPartStart);

/// <summary>基于完整轨迹几何每 12 米取样；与地图缩放、显示抽稀无关。</summary>
public sealed class DirectionArrowCache
{
    public const double SpacingMeters = 12;
    private readonly Dictionary<Track, (long Revision, DirectionArrow[] Arrows)> _entries =
        new(ReferenceEqualityComparer.Instance);
    private readonly object _gate = new();

    public IReadOnlyList<DirectionArrow> Get(Track track, CancellationToken token = default)
    {
        var revision = track.GeometryRevision;
        lock (_gate)
            if (_entries.TryGetValue(track, out var entry) && entry.Revision == revision)
                return entry.Arrows;
        var arrows = Sample(track, token);
        token.ThrowIfCancellationRequested();
        if (track.GeometryRevision != revision)
            throw new OperationCanceledException("轨迹在方向箭头计算期间发生变化。", token);
        lock (_gate) _entries[track] = (revision, arrows);
        return arrows;
    }

    public void Invalidate(Guid trackId)
    {
        lock (_gate)
            foreach (var track in _entries.Keys.Where(track => track.Id == trackId).ToArray())
                _entries.Remove(track);
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
    }

    public static DirectionArrow[] Sample(Track track, CancellationToken token = default)
    {
        var distance = GeoDistanceCalculator.Instance;
        var arrows = new List<DirectionArrow>();
        foreach (var part in track.Segments)
        {
            var travelled = 0d;
            var next = SpacingMeters;
            for (var index = 1; index < part.Points.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var from = part.Points[index - 1].Coordinate;
                var to = part.Points[index].Coordinate;
                var length = distance.CalculateDistance(from, to);
                if (length <= 1e-8) continue;
                var bearing = Bearing(from, to);
                while (next <= travelled + length + 1e-8)
                {
                    var fraction = Math.Clamp((next - travelled) / length, 0, 1);
                    arrows.Add(new DirectionArrow(track.Id, part.Id,
                        new GeoCoordinate(from.Latitude + (to.Latitude - from.Latitude) * fraction,
                            from.Longitude + (to.Longitude - from.Longitude) * fraction),
                        bearing, next));
                    next += SpacingMeters;
                }
                travelled += length;
            }
        }
        return arrows.ToArray();
    }

    private static double Bearing(GeoCoordinate from, GeoCoordinate to)
    {
        var latitude = (from.Latitude + to.Latitude) * Math.PI / 360;
        var east = (to.Longitude - from.Longitude) * Math.Cos(latitude);
        var north = to.Latitude - from.Latitude;
        return (Math.Atan2(east, north) * 180 / Math.PI + 360) % 360;
    }
}
