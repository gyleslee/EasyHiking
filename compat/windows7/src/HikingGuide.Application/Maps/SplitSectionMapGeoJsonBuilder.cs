using System.Text.Json;
using HikingGuide.Core.Models;

namespace HikingGuide.Application.Maps;

public sealed class SplitSectionMapGeoJsonBuilder
{
    public string Build(HikingProject project, MapDisplayData display)
    {
        var features = new List<object>();
        foreach (var rendered in display.Tracks)
        {
            var track = project.Tracks.FirstOrDefault(item => item.Id == rendered.TrackId);
            if (track is null || track.Segments.Count < 2) continue;
            foreach (var section in track.GetSplitSections())
            {
                var coordinates = section.GeometryParts.Select(part =>
                {
                    var index = track.Segments.IndexOf(part);
                    return rendered.Segments[index].Select(point => new[] { point.Longitude, point.Latitude }).ToArray();
                }).ToArray();
                features.Add(new
                {
                    type = "Feature", id = section.Id,
                    properties = new { splitSectionId = section.Id, trackId = track.Id,
                        geometryRevision = track.GeometryRevision, name = section.Name },
                    geometry = new { type = "MultiLineString", coordinates }
                });
            }
        }
        return JsonSerializer.Serialize(new { type = "FeatureCollection", features });
    }
}
