using System.Text.Json;
using HikingGuide.Core.Models;

namespace HikingGuide.Application.Maps;

public sealed class DirectionArrowGeoJsonBuilder(DirectionArrowCache cache)
{
    public string Build(HikingProject project, Guid? temporarilyVisibleTrackId = null,
        CancellationToken cancellationToken = default)
    {
        var features = new List<object>();
        foreach (var track in project.Tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!MapLayerVisibility.IsTrackVisible(project.Settings.Map, track.Id) &&
                track.Id != temporarilyVisibleTrackId) continue;
            foreach (var arrow in cache.Get(track, cancellationToken))
                features.Add(new
                {
                    type = "Feature",
                    properties = new { trackId = arrow.TrackId, segmentId = arrow.SegmentId,
                        bearing = arrow.BearingDegrees },
                    geometry = new { type = "Point", coordinates = new[]
                    { arrow.Coordinate.Longitude, arrow.Coordinate.Latitude } }
                });
        }
        return JsonSerializer.Serialize(new { type = "FeatureCollection", features });
    }
}
