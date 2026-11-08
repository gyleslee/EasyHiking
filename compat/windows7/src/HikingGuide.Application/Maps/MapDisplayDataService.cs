using HikingGuide.Core.Models;

namespace HikingGuide.Application.Maps;

public sealed class MapDisplayDataService(ITrackDisplaySimplifier simplifier)
{
    public void InvalidateTrack(Guid trackId) => simplifier.Invalidate(trackId);
    public MapDisplayData Create(
        HikingProject project,
        double zoom,
        long revision,
        CancellationToken cancellationToken = default,
        Guid? temporarilyVisibleTrackId = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        var tracks = new List<MapTrackFeature>(project.Tracks.Count);
        foreach (var track in project.Tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!MapLayerVisibility.IsTrackVisible(project.Settings.Map, track.Id) &&
                track.Id != temporarilyVisibleTrackId) continue;
            tracks.Add(simplifier.Simplify(track, track.Id == project.MainGuideTrackId, zoom, cancellationToken)
                with { GeometryRevision = track.GeometryRevision });
        }
        var waypoints = project.Waypoints.Where(waypoint => waypoint.AssociatedTrackId is not Guid owner ||
            MapLayerVisibility.IsTrackVisible(project.Settings.Map, owner)).Select(waypoint => new MapWaypointFeature(
            waypoint.Id,
            waypoint.Name ?? "未命名标注",
            waypoint.Type,
            new MapCoordinate(waypoint.Longitude, waypoint.Latitude))).ToArray();
        return new MapDisplayData(project.Id, revision, zoom, tracks, waypoints);
    }
}
