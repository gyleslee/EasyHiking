using System.Text.Json;
using HikingGuide.Application.Guides;
using HikingGuide.Core.Models;

namespace HikingGuide.Application.Maps;

public sealed class GuideMapGeoJsonBuilder
{
    public (JsonElement Nodes, JsonElement Segments) Build(HikingProject project,bool includeGeometry=true)
    {
        var order = project.GuideNodes.Where(node => node.TrackId == project.MainGuideTrackId && !node.IsOrphaned)
            .OrderBy(node => node.ExactPosition?.DistanceFromTrackStart ?? double.PositiveInfinity)
            .Select((node, index) => (node.Id, Order: index + 1)).ToDictionary(value => value.Id, value => value.Order);
        var nodes = project.GuideNodes.Where(node => node.TrackId is not Guid owner ||
            MapLayerVisibility.IsTrackVisible(project.Settings.Map, owner)).Select(node => new
        {
            type = "Feature",
            properties = new
            {
                guideNodeId = node.Id.ToString("D"),
                name = node.Name ?? "未命名攻略节点",
                namedLabel = node.Name?.Trim() ?? "",
                order = order.GetValueOrDefault(node.Id),
                isOrphaned = node.IsOrphaned,
                markerType = node.Type.ToString(),
                mediaInternalCount = project.MediaAttachments.Count(item => item.OwnerId == node.Id),
                mediaExternalCount = project.ExternalMediaReferences.Count(item => item.OwnerId == node.Id),
                mediaHost = project.ExternalMediaReferences.Where(item => item.OwnerId == node.Id)
                    .Select(item => Uri.TryCreate(item.Url, UriKind.Absolute, out var url) ? url.Host : "")
                    .FirstOrDefault() ?? ""
            },
            geometry = new
            {
                type = "Point",
                coordinates = new[] { (node.IsOrphaned ? node.OriginalCoordinate : node.SnappedCoordinate ?? node.OriginalCoordinate).Longitude,
                    (node.IsOrphaned ? node.OriginalCoordinate : node.SnappedCoordinate ?? node.OriginalCoordinate).Latitude }
            }
        }).ToArray();
        var extractor = new GuideStructureService();
        var segments = new List<object>();
        foreach (var segment in project.GuideSegments)
        {
            if (segment.TrackId is Guid parent && !MapLayerVisibility.IsTrackVisible(project.Settings.Map, parent)) continue;
            if (!MapLayerVisibility.IsGuideSegmentVisible(project.Settings.Map, segment.Id)) continue;
            var start = project.GuideNodes.FirstOrDefault(node => node.Id == segment.StartNodeId);
            var end = project.GuideNodes.FirstOrDefault(node => node.Id == segment.EndNodeId);
            var track = project.Tracks.FirstOrDefault(t => t.Id == segment.TrackId);
            if (start?.ExactPosition is null || end?.ExactPosition is null || track is null || start.IsOrphaned || end.IsOrphaned) continue;
            var pieces = includeGeometry?extractor.ExtractRange(track, start.ExactPosition, end.ExactPosition):Array.Empty<IReadOnlyList<HikingGuide.Core.Geometry.GeoCoordinate>>();
            segments.Add(new
            {
                type = "Feature",
                properties = new { guideSegmentId = segment.Id.ToString("D"), trackId = track.Id.ToString("D"), startNodeId = start.Id.ToString("D"), endNodeId = end.Id.ToString("D") },
                geometry = new
                {
                    type = "MultiLineString",
                    coordinates = pieces.Select(piece => piece.Select(point => new[] { point.Longitude, point.Latitude }).ToArray()).ToArray()
                }
            });
        }
        return (JsonSerializer.SerializeToElement(new { type = "FeatureCollection", features = nodes }),
            JsonSerializer.SerializeToElement(new { type = "FeatureCollection", features = segments }));
    }
}
