using HikingGuide.Core.Models;
using HikingGuide.Core.Services;
using HikingGuide.Application.Guides;

namespace HikingGuide.Application.TrackEditing;

/// <summary>仅用于合并/连接删除来源；独立删除轨迹仍沿用原规则。</summary>
internal sealed class TrackMergeMarkerTransfer
{
    private readonly (Waypoint Point, Guid? Owner)[] _points;
    private readonly (GuideNode Node, Guid? Owner, string? Name)[] _sources;
    public TrackMergeMarkerTransfer(HikingProject project, IReadOnlyList<TrackMergeRemoval> removals)
    {
        var snap = new TrackSnapService();
        bool Included(Guid? owner, HikingGuide.Core.Geometry.GeoCoordinate coordinate)
        {
            var removal = removals.FirstOrDefault(r => r.Track.Id == owner);
            if (removal is null) return false;
            if (removal.WholeTrack) return true;
            var part = snap.Snap(removal.Track, coordinate).Position?.TrackSegmentId;
            return part.HasValue && removal.Sections.Any(s => s.GeometryParts.Any(p => p.Id == part));
        }
        _points = project.Waypoints.Where(p => Included(p.AssociatedTrackId, p.Coordinate))
            .Select(p => (p, p.AssociatedTrackId)).ToArray();
        _sources = project.GuideNodes.Where(n => Included(n.SourceTrackId, n.OriginalCoordinate))
            .Select(n => (n, n.SourceTrackId, n.SourceTrackName)).ToArray();
    }
    public int Count => _points.Length;
    public void Apply(Track result)
    {
        foreach (var item in _points) item.Point.AssociatedTrackId = result.Id;
        foreach (var item in _sources) { item.Node.SourceTrackId = result.Id; item.Node.SourceTrackName = result.Name; }
    }
    public void Undo()
    {
        foreach (var item in _points) item.Point.AssociatedTrackId = item.Owner;
        foreach (var item in _sources) { item.Node.SourceTrackId = item.Owner; item.Node.SourceTrackName = item.Name; }
    }
}
