using System.Xml;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using HikingGuide.Application.TrackFiles;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;

namespace HikingGuide.Infrastructure.TrackFiles;

public sealed class GpxTrackImporter : ITrackImporter
{
    public TrackFileFormat Format => TrackFileFormat.Gpx;

    public async Task<TrackImportResult> ImportAsync(
        string filePath,
        IProgress<TrackOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new TrackOperationProgress("正在解析GPX轨迹……"));
        var document = await XmlSupport.LoadSecureDocumentAsync(filePath, cancellationToken)
            .ConfigureAwait(false);
        var root = document.Root;
        if (root is null || root.Name.LocalName != "gpx")
        {
            throw new XmlException("文件根节点不是GPX。 ");
        }

        var result = new TrackImportResult
        {
            SourceFile = Path.GetFullPath(filePath),
            SourceFormat = TrackFileFormat.Gpx
        };
        PreserveFileMetadata(root, result.Metadata);

        var trackIndex = 0;
        foreach (var trackElement in XmlSupport.Children(root, "trk"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            trackIndex++;
            result.Tracks.Add(ParseTrack(trackElement, filePath, trackIndex, result, cancellationToken));
        }

        var waypointIndex = 0;
        foreach (var waypointElement in XmlSupport.Children(root, "wpt"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            waypointIndex++;
            var waypoint = ParseWaypoint(waypointElement, waypointIndex, result);
            if (waypoint is not null)
            {
                result.Waypoints.Add(waypoint);
                var order = 0;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var media in waypointElement.Descendants().Where(item => item.Name.LocalName == "externalMedia"))
                {
                    if (!Uri.TryCreate(media.Value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
                        !seen.Add(media.Value)) continue;
                    var itemOrder = int.TryParse(media.Attribute("order")?.Value, out var parsed) && parsed >= 0 ? parsed : order;
                    result.ExternalMediaReferences.Add(new ExternalMediaReference
                    { OwnerId = waypoint.Id, Url = media.Value, Caption = media.Attribute("caption")?.Value,
                        SourceKind = "GpxExtension", SourceField = "externalMedia", DisplayOrder = itemOrder });
                    order++;
                }
                foreach (Match match in Regex.Matches(waypoint.Description ?? "", "https?://[^\\s<>\\\"']+", RegexOptions.IgnoreCase))
                {
                    var url = match.Value.TrimEnd('.', ',', ';', ')');
                    if (!seen.Add(url)) continue;
                    result.ExternalMediaReferences.Add(new ExternalMediaReference
                    { OwnerId = waypoint.Id, Url = url, SourceKind = "GpxDescription",
                        SourceField = "desc", DisplayOrder = order++ });
                }
            }
        }

        return result;
    }

    private static Track ParseTrack(
        XElement element,
        string sourceFile,
        int trackIndex,
        TrackImportResult result,
        CancellationToken cancellationToken)
    {
        var track = new Track
        {
            Name = XmlSupport.ChildValue(element, "name"),
            Description = XmlSupport.ReadRichContent(element, "desc"),
            SourceFile = Path.GetFullPath(sourceFile),
            SourceFormat = TrackSourceFormat.Gpx
        };
        CopyStringMetadata(element, track.Metadata, "cmt", "src", "type", "number");
        PreserveExtension(element, track.Metadata, result);
        if (element.Descendants().FirstOrDefault(item => item.Name.LocalName == "trackKey") is { } trackKey)
            track.Metadata["hg:trackKey"] = trackKey.Value;

        var segmentIndex = 0;
        foreach (var segmentElement in XmlSupport.Children(element, "trkseg"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            segmentIndex++;
            var segment = new TrackSegment();
            XmlSupport.PreserveAttributes(segmentElement, segment.OriginalAttributes, "gpx:attribute:");
            PreserveExtension(segmentElement, segment.OriginalAttributes, result);
            PreserveUnknownChildren(
                segmentElement,
                segment.OriginalAttributes,
                new HashSet<string>(StringComparer.Ordinal) { "trkpt", "extensions" });
            var pointIndex = 0;
            foreach (var pointElement in XmlSupport.Children(segmentElement, "trkpt"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                pointIndex++;
                var point = ParseTrackPoint(pointElement, trackIndex, segmentIndex, pointIndex, result);
                if (point is not null)
                {
                    segment.Points.Add(point);
                }
            }

            track.Segments.Add(segment);
        }

        return track;
    }

    private static TrackPoint? ParseTrackPoint(
        XElement element,
        int trackIndex,
        int segmentIndex,
        int pointIndex,
        TrackImportResult result)
    {
        var context = $"轨迹 {trackIndex} / 轨迹段 {segmentIndex} / 点 {pointIndex}";
        if (!TryReadCoordinate(element, context, result, out var coordinate))
        {
            return null;
        }

        var point = new TrackPoint
        {
            Latitude = coordinate.Latitude,
            Longitude = coordinate.Longitude,
            SourceElevation = ReadElevation(element, context, result),
            Timestamp = ReadTimestamp(element, context, result)
        };
        XmlSupport.PreserveAttributes(element, point.OriginalAttributes, "gpx:attribute:", "lat", "lon");
        PreservePointAttributes(element, point.OriginalAttributes, result);
        return point;
    }

    private static Waypoint? ParseWaypoint(
        XElement element,
        int waypointIndex,
        TrackImportResult result)
    {
        var context = $"标注点 {waypointIndex}";
        if (!TryReadCoordinate(element, context, result, out var coordinate))
        {
            return null;
        }

        var type = XmlSupport.ChildValue(element, "type");
        var symbol = XmlSupport.ChildValue(element, "sym");
        var waypoint = new Waypoint
        {
            Name = XmlSupport.ChildValue(element, "name"),
            Description = XmlSupport.ReadRichContent(element, "desc"),
            Latitude = coordinate.Latitude,
            Longitude = coordinate.Longitude,
            SourceElevation = ReadElevation(element, context, result),
            Timestamp = ReadTimestamp(element, context, result),
            Type = WaypointTypeMapper.FromExplicitValues(type, symbol)
        };

        XmlSupport.PreserveAttributes(element, waypoint.OriginalAttributes, "gpx:attribute:", "lat", "lon");

        CopyOriginalValue(element, waypoint.OriginalAttributes, "cmt");
        CopyOriginalValue(element, waypoint.OriginalAttributes, "src");
        CopyOriginalValue(element, waypoint.OriginalAttributes, "sym");
        CopyOriginalValue(element, waypoint.OriginalAttributes, "type");
        PreserveExtension(element, waypoint.OriginalAttributes, result);
        if (element.Descendants().FirstOrDefault(item => item.Name.LocalName == "isGuideNode") is { } guideFlag)
            waypoint.OriginalAttributes["hg:isGuideNode"] = guideFlag.Value;
        if (element.Descendants().FirstOrDefault(item => item.Name.LocalName == "relatedTrackKey") is { } relatedKey)
            waypoint.OriginalAttributes["hg:relatedTrackKey"] = relatedKey.Value;
        if (element.Descendants().FirstOrDefault(item => item.Name.LocalName == "markerType") is { } markerType &&
            Enum.TryParse<WaypointType>(markerType.Value, true, out var businessType)) waypoint.Type = businessType;
        if (element.Descendants().FirstOrDefault(item => item.Name.LocalName == "customTypeName") is { } customType)
            waypoint.CustomTypeName = customType.Value;
        PreserveUnknownChildren(
            element,
            waypoint.OriginalAttributes,
            new HashSet<string>(StringComparer.Ordinal)
            {
                "ele", "time", "name", "desc", "cmt", "src", "sym", "type", "extensions"
            });
        return waypoint;
    }

    private static bool TryReadCoordinate(
        XElement element,
        string context,
        TrackImportResult result,
        out GeoCoordinate coordinate)
    {
        coordinate = default;
        if (!XmlSupport.TryParseDouble(element.Attribute("lat")?.Value, out var latitude) ||
            !XmlSupport.TryParseDouble(element.Attribute("lon")?.Value, out var longitude))
        {
            result.Warnings.Add(new ImportWarning("InvalidCoordinate", "坐标缺失或格式无效，已跳过该点。", context));
            return false;
        }

        try
        {
            coordinate = new GeoCoordinate(latitude, longitude);
            coordinate.Validate();
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            result.Warnings.Add(new ImportWarning("InvalidCoordinate", "坐标超出有效范围，已跳过该点。", context));
            return false;
        }
    }

    private static double? ReadElevation(XElement element, string context, TrackImportResult result)
    {
        var value = XmlSupport.ChildValue(element, "ele");
        if (value is null)
        {
            return null;
        }

        if (XmlSupport.TryParseDouble(value, out var elevation))
        {
            return elevation;
        }

        result.Warnings.Add(new ImportWarning("InvalidElevation", "海拔格式无效，已保留为空。", context));
        return null;
    }

    private static DateTimeOffset? ReadTimestamp(XElement element, string context, TrackImportResult result)
    {
        var value = XmlSupport.ChildValue(element, "time");
        if (value is null)
        {
            return null;
        }

        if (XmlSupport.TryParseTimestamp(value, out var timestamp))
        {
            return timestamp;
        }

        result.Warnings.Add(new ImportWarning("InvalidTimestamp", "时间格式无效，已保留为空。", context));
        return null;
    }

    private static void PreservePointAttributes(
        XElement element,
        IDictionary<string, string?> attributes,
        TrackImportResult result)
    {
        CopyOriginalValue(element, attributes, "name");
        CopyOriginalValue(element, attributes, "cmt");
        CopyOriginalValue(element, attributes, "desc");
        CopyOriginalValue(element, attributes, "src");
        CopyOriginalValue(element, attributes, "sym");
        CopyOriginalValue(element, attributes, "type");
        PreserveExtension(element, attributes, result);
        PreserveUnknownChildren(
            element,
            attributes,
            new HashSet<string>(StringComparer.Ordinal)
            {
                "ele", "time", "name", "cmt", "desc", "src", "sym", "type", "extensions"
            });
    }

    private static void PreserveFileMetadata(XElement root, IDictionary<string, string?> metadata)
    {
        metadata["gpx:version"] = root.Attribute("version")?.Value;
        metadata["gpx:creator"] = root.Attribute("creator")?.Value;
        var metadataElement = XmlSupport.Child(root, "metadata");
        if (metadataElement is null)
        {
            return;
        }

        CopyStringMetadata(metadataElement, metadata, "name", "desc", "author", "copyright", "link", "time", "keywords", "bounds");
        XmlSupport.PreserveElement(metadata, "gpx:metadata:extensions", XmlSupport.Child(metadataElement, "extensions"));
    }

    private static void CopyStringMetadata(
        XElement element,
        IDictionary<string, string?> metadata,
        params string[] names)
    {
        foreach (var name in names)
        {
            var child = XmlSupport.Child(element, name);
            if (child is not null)
            {
                metadata[$"gpx:{name}"] = child.HasElements
                    ? child.ToString(SaveOptions.DisableFormatting)
                    : child.Value;
            }
        }
    }

    private static void CopyOriginalValue(
        XElement element,
        IDictionary<string, string?> attributes,
        string localName)
    {
        var value = XmlSupport.ChildValue(element, localName);
        if (value is not null)
        {
            attributes[$"gpx:{localName}"] = value;
        }
    }

    private static void PreserveExtension(
        XElement element,
        IDictionary<string, string?> attributes,
        TrackImportResult result)
    {
        var extension = XmlSupport.Child(element, "extensions");
        if (extension is null)
        {
            return;
        }

        XmlSupport.PreserveElement(attributes, "gpx:extensions", extension);
        if (result.Warnings.All(warning => warning.Code != "PreservedGpxExtension"))
        {
            result.Warnings.Add(new ImportWarning(
                "PreservedGpxExtension",
                "发现未解析的GPX扩展字段，已保留原始XML。"));
        }
    }

    private static void PreserveUnknownChildren(
        XElement element,
        IDictionary<string, string?> attributes,
        IReadOnlySet<string> knownNames)
    {
        foreach (var child in element.Elements().Where(child => !knownNames.Contains(child.Name.LocalName)))
        {
            XmlSupport.PreserveElement(attributes, $"gpx:xml:{child.Name.LocalName}", child);
        }
    }
}
