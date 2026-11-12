using System.Buffers;
using System.Text;
using System.Text.Json;
using HikingGuide.Core.Models;

namespace HikingGuide.Application.Maps;

public sealed record MapGeoJsonPayload(string TracksGeoJson, string WaypointsGeoJson);

public sealed class MapGeoJsonBuilder
{
    public MapGeoJsonPayload Build(MapDisplayData data, HikingProject? project = null) =>
        new(BuildTracks(data,project), BuildWaypoints(data, project));

    public string BuildTracks(MapDisplayData data,HikingProject? projectForVisibility=null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "FeatureCollection");
            writer.WritePropertyName("features");
            writer.WriteStartArray();
            foreach (var track in data.Tracks)
            {
              var chunkIndex=0;
              foreach(var piece in track.RenderPieces ?? new[]{new MapRenderPiece(Guid.Empty,null,Array.Empty<MapCoordinate>())})
              {
                writer.WriteStartObject();
                writer.WriteString("type", "Feature");
                writer.WriteString("id",track.RenderPieces is null?track.TrackId.ToString():$"{track.TrackId}:{chunkIndex}");
                writer.WritePropertyName("properties");
                writer.WriteStartObject();
                writer.WriteString("trackId", track.TrackId);
                writer.WriteString("chunkId",$"{track.TrackId}:{chunkIndex++}");
                writer.WriteNumber("geometryRevision", track.GeometryRevision);
                writer.WriteString("name", track.Name);
                writer.WriteBoolean("isActive", track.IsActive);
                writer.WriteString("color", TrackDisplayColor.IsValid(track.DisplayColor) ? track.DisplayColor : "#2877C7");
                writer.WriteNumber("originalPointCount", track.OriginalPointCount);
                writer.WriteNumber("displayPointCount", track.DisplayPointCount);
                writer.WriteString("splitSectionId",piece.SplitSectionId);
                writer.WriteBoolean("hasSplitSections",track.Segments.Count>1);
                if(piece.GuideSegmentId is Guid guideId)writer.WriteString("guideSegmentId",guideId);
                writer.WriteBoolean("guideVisible",piece.GuideSegmentId is Guid visibleId && (projectForVisibility is null || MapLayerVisibility.IsGuideSegmentVisible(projectForVisibility.Settings.Map,visibleId)));
                writer.WriteEndObject();
                writer.WritePropertyName("geometry");
                writer.WriteStartObject();
                writer.WriteString("type", "MultiLineString");
                writer.WritePropertyName("coordinates");
                writer.WriteStartArray();
                foreach (var segment in track.RenderPieces is null ? track.Segments : new[]{piece.Coordinates})
                {
                    writer.WriteStartArray();
                    foreach (var point in segment)
                    {
                        writer.WriteStartArray();
                        writer.WriteNumberValue(point.Longitude);
                        writer.WriteNumberValue(point.Latitude);
                        writer.WriteEndArray();
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WriteEndObject();
              }
            }
            writer.WriteEndArray();
            writer.WriteString("projectId", data.ProjectId);
            writer.WriteNumber("revision", data.Revision);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public string BuildWaypoints(MapDisplayData data, HikingProject? project = null)
    {
        var descriptions=project?.Waypoints.ToDictionary(p=>p.Id,p=>p.Description);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "FeatureCollection");
            writer.WritePropertyName("features");
            writer.WriteStartArray();
            foreach (var waypoint in data.Waypoints)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "Feature");
                writer.WriteString("id", waypoint.WaypointId);
                writer.WritePropertyName("properties");
                writer.WriteStartObject();
                writer.WriteString("waypointId", waypoint.WaypointId);
                writer.WriteString("name", waypoint.Name);
                writer.WriteString("namedLabel", waypoint.Name?.Trim() ?? "");
                writer.WriteString("description", descriptions?.GetValueOrDefault(waypoint.WaypointId)??"");
                writer.WriteString("waypointType", waypoint.Type.ToString());
                var attachments = project?.MediaAttachments.Count(item => item.OwnerId == waypoint.WaypointId) ?? 0;
                var external = project?.ExternalMediaReferences.Where(item => item.OwnerId == waypoint.WaypointId).ToArray() ?? [];
                writer.WriteNumber("mediaInternalCount", attachments);
                writer.WriteNumber("mediaExternalCount", external.Length);
                if (external.FirstOrDefault() is { } reference &&
                    Uri.TryCreate(reference.Url, UriKind.Absolute, out var url)) writer.WriteString("mediaHost", url.Host);
                writer.WriteEndObject();
                writer.WritePropertyName("geometry");
                writer.WriteStartObject();
                writer.WriteString("type", "Point");
                writer.WritePropertyName("coordinates");
                writer.WriteStartArray();
                writer.WriteNumberValue(waypoint.Coordinate.Longitude);
                writer.WriteNumberValue(waypoint.Coordinate.Latitude);
                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteString("projectId", data.ProjectId);
            writer.WriteNumber("revision", data.Revision);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
