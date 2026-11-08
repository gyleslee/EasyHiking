using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;

namespace HikingGuide.Application.TrackEditing;

public sealed record TrackEditDisplayProfile(
    double HandleDisplayZoom = 14,
    int MaxVisibleHandles = 40,
    int NeighborHandleCount = 6,
    double PickTolerancePixels = 14,
    int MaxRangePreviewPoints = 8000)
{
    public void Validate()
    {
        if (!double.IsFinite(HandleDisplayZoom) || HandleDisplayZoom < 0 || HandleDisplayZoom > 24 ||
            MaxVisibleHandles is < 1 or > 1000 || NeighborHandleCount < 0 ||
            NeighborHandleCount * 2 + 1 > MaxVisibleHandles ||
            MaxRangePreviewPoints is < 100 or > 100000 ||
            !double.IsFinite(PickTolerancePixels) || PickTolerancePixels is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(TrackEditDisplayProfile), "轨迹编辑显示参数无效。");
    }
}

public sealed record TrackEditHandle(Guid PointId, Guid SegmentId, int SegmentIndex,
    int PointIndex, GeoCoordinate Coordinate, bool Selected);

public sealed record TrackPointEditPick(TrackPosition LinePosition, TrackEditHandle SelectedPoint,
    IReadOnlyList<TrackEditHandle> VisibleHandles);

/// <summary>从完整 Working Track 的空间索引取真实点；与地图简化线无关。</summary>
public sealed class TrackPointEditIndex(TrackEditDisplayProfile? profile = null)
{
    private readonly TrackEditDisplayProfile _profile = profile ?? new TrackEditDisplayProfile();
    private readonly TrackSnapService _snap = new();

    public TrackPointEditPick? Pick(Track track, GeoCoordinate coordinate, double zoom)
    {
        _profile.Validate();
        coordinate.Validate();
        if (!double.IsFinite(zoom) || zoom is < 0 or > 24)
            throw new ArgumentOutOfRangeException(nameof(zoom));
        var position = _snap.Snap(track, coordinate).Position;
        if (position is null) return null;
        var metersPerPixel = 156543.03392 * Math.Cos(coordinate.Latitude * Math.PI / 180) /
            Math.Pow(2, zoom);
        if (position.DistanceToTrack > metersPerPixel * _profile.PickTolerancePixels) return null;
        var segment = track.Segments[position.SegmentIndex];
        var a = segment.Points[position.StartPointIndex];
        var b = segment.Points[position.EndPointIndex];
        var distance = GeoDistanceCalculator.Instance;
        var pointIndex = distance.CalculateDistance(coordinate, a.Coordinate) <=
            distance.CalculateDistance(coordinate, b.Coordinate)
                ? position.StartPointIndex : position.EndPointIndex;
        var first = Math.Max(0, pointIndex - _profile.NeighborHandleCount);
        var last = Math.Min(segment.Points.Count - 1, pointIndex + _profile.NeighborHandleCount);
        var handles = new List<TrackEditHandle>(last - first + 1);
        for (var index = first; index <= last; index++)
        {
            var point = segment.Points[index];
            handles.Add(new TrackEditHandle(point.Id, segment.Id, position.SegmentIndex,
                index, point.Coordinate, index == pointIndex));
        }
        return new TrackPointEditPick(position, handles.Single(handle => handle.Selected), handles);
    }

    public void Invalidate(Guid trackId) => _snap.Release(trackId);
}
