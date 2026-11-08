using System.Xml;
using System.Xml.Linq;
using HikingGuide.Application.TrackFiles;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using HikingGuide.Application.Media;

namespace HikingGuide.Infrastructure.TrackFiles;

public sealed class KmlTrackImporter(IImageDecodeValidator? imageValidator = null) : ITrackImporter
{
    private readonly IImageDecodeValidator? _imageValidator = imageValidator;
    public TrackFileFormat Format => TrackFileFormat.Kml;

    public async Task<TrackImportResult> ImportAsync(
        string filePath,
        IProgress<TrackOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new TrackOperationProgress("正在解析KML轨迹……"));
        var document = await XmlSupport.LoadSecureDocumentAsync(filePath, cancellationToken)
            .ConfigureAwait(false);
        return ImportDocument(document, filePath, null, cancellationToken);
    }

    internal TrackImportResult ImportDocument(XDocument document, string filePath,
        Func<string, CancellationToken, Task<byte[]?>>? embeddedImageResolver,
        CancellationToken cancellationToken = default)
    {
        var root = document.Root;
        if (root is null || root.Name.LocalName != "kml")
        {
            throw new XmlException("文件根节点不是KML。 ");
        }

        var result = new TrackImportResult
        {
            SourceFile = Path.GetFullPath(filePath),
            SourceFormat = TrackFileFormat.Kml
        };

        var topContainer = root.Elements().FirstOrDefault(element =>
            element.Name.LocalName is "Document" or "Folder");
        if (topContainer is not null)
        {
            PreserveContainerMetadata(topContainer, result.Metadata);
            ParseContainer(topContainer, [], filePath, result, embeddedImageResolver, cancellationToken);
        }
        else
        {
            ParseContainer(root, [], filePath, result, embeddedImageResolver, cancellationToken);
        }

        // 仅当同一个非空 KML Folder 中恰有一条轨迹时，才可确定其标注归属。
        foreach (var waypoint in result.Waypoints)
        {
            if (!waypoint.OriginalAttributes.TryGetValue("kml:folderPath", out var path) ||
                string.IsNullOrWhiteSpace(path)) continue;
            var matches = result.Tracks.Where(track => track.Metadata.GetValueOrDefault("kml:folderPath") == path).ToArray();
            if (matches.Length == 1) waypoint.AssociatedTrackId = matches[0].Id;
        }

        return result;
    }

    private void ParseContainer(
        XContainer container,
        IReadOnlyList<string> folderPath,
        string sourceFile,
        TrackImportResult result,
        Func<string, CancellationToken, Task<byte[]?>>? embeddedImageResolver,
        CancellationToken cancellationToken)
    {
        foreach (var child in container.Elements())
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (child.Name.LocalName)
            {
                case "Document":
                    ParseContainer(child, folderPath, sourceFile, result, embeddedImageResolver, cancellationToken);
                    break;
                case "Folder":
                    var folderName = XmlSupport.ChildValue(child, "name");
                    var nextPath = folderName is null
                        ? folderPath
                        : folderPath.Concat([folderName]).ToArray();
                    ParseContainer(child, nextPath, sourceFile, result, embeddedImageResolver, cancellationToken);
                    break;
                case "Placemark":
                    ParsePlacemark(child, folderPath, sourceFile, result, embeddedImageResolver, cancellationToken);
                    break;
            }
        }
    }

    private void ParsePlacemark(
        XElement placemark,
        IReadOnlyList<string> folderPath,
        string sourceFile,
        TrackImportResult result,
        Func<string, CancellationToken, Task<byte[]?>>? embeddedImageResolver,
        CancellationToken cancellationToken)
    {
        var name = XmlSupport.ChildValue(placemark, "name");
        var description = XmlSupport.ReadRichContent(placemark, "description");
        var styleUrl = XmlSupport.ChildValue(placemark, "styleUrl");
        var lineStrings = placemark.Descendants()
            .Where(element => element.Name.LocalName == "LineString" ||
                              element.Name.LocalName == "Track" && element.Name.NamespaceName == "http://www.google.com/kml/ext/2.2")
            .ToArray();
        var points = placemark.Descendants()
            .Where(element => element.Name.LocalName == "Point")
            .ToArray();

        if (name is null && (lineStrings.Length > 0 || points.Length > 0))
        {
            result.Warnings.Add(new ImportWarning(
                "UnnamedPlacemark",
                "Placemark缺少名称，已按未命名对象读取。"));
        }

        if (lineStrings.Length > 0)
        {
            var track = new Track
            {
                Name = name,
                Description = description,
                SourceFile = Path.GetFullPath(sourceFile),
                SourceFormat = TrackSourceFormat.Kml
            };
            PreservePlacemarkMetadata(placemark, track.Metadata, folderPath);
            var trackKey = BusinessValue(placemark, "hg:trackKey");
            if (trackKey is not null) track.Metadata["hg:trackKey"] = trackKey;

            for (var lineIndex = 0; lineIndex < lineStrings.Length; lineIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isGxTrack = lineStrings[lineIndex].Name.LocalName == "Track";
                var attributePrefix = isGxTrack ? "kml:gx:Track:attribute:" : "kml:LineString:attribute:";
                var elementPrefix = isGxTrack ? "kml:gx:Track:xml:" : "kml:LineString:xml:";
                var segment = new TrackSegment
                {
                    Name = lineStrings.Length > 1 ? $"轨迹段 {lineIndex + 1}" : null
                };
                XmlSupport.PreserveAttributes(
                    lineStrings[lineIndex],
                    segment.OriginalAttributes,
                    attributePrefix);
                foreach (var child in lineStrings[lineIndex].Elements().Where(child =>
                             child.Name.LocalName != "coordinates" &&
                             (!isGxTrack || child.Name.LocalName is not ("coord" or "when"))))
                {
                    XmlSupport.PreserveElement(
                        segment.OriginalAttributes,
                        $"{elementPrefix}{child.Name.LocalName}",
                        child);
                }
                if (isGxTrack)
                {
                    var times = XmlSupport.Children(lineStrings[lineIndex], "when").ToArray();
                    var coords = XmlSupport.Children(lineStrings[lineIndex], "coord").ToArray();
                    if (times.Length > 0 && times.Length != coords.Length)
                        result.Warnings.Add(new ImportWarning("GxTrackTimeCount", "gx:Track时间与坐标数量不一致，未匹配的点不附加时间。", name));
                    for (var pointIndex = 0; pointIndex < coords.Length; pointIndex++)
                    {
                        if ((pointIndex & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                        var coordinate = ParseGxCoordinate(coords[pointIndex].Value, result, name ?? "gx:Track");
                        if (coordinate is null) continue;
                        segment.Points.Add(new TrackPoint
                        {
                            Latitude = coordinate.Coordinate.Latitude,
                            Longitude = coordinate.Coordinate.Longitude,
                            SourceElevation = coordinate.Altitude,
                            Timestamp = pointIndex < times.Length && XmlSupport.TryParseTimestamp(times[pointIndex].Value, out var time)
                                ? time : null
                        });
                    }
                    track.Segments.Add(segment);
                    continue;
                }
                var coordinateElement = XmlSupport.Child(lineStrings[lineIndex], "coordinates");
                if (coordinateElement is null)
                {
                    result.Warnings.Add(new ImportWarning(
                        "MissingCoordinates",
                        "LineString缺少coordinates，已保留为空轨迹段。",
                        name));
                }
                else
                {
                    foreach (var coordinate in ParseCoordinates(
                                 coordinateElement.Value,
                                 result,
                                 name ?? $"LineString {lineIndex + 1}"))
                    {
                        segment.Points.Add(new TrackPoint
                        {
                            Latitude = coordinate.Coordinate.Latitude,
                            Longitude = coordinate.Coordinate.Longitude,
                            SourceElevation = coordinate.Altitude
                        });
                    }
                }

                track.Segments.Add(segment);
            }

            result.Tracks.Add(track);
        }

        for (var pointIndex = 0; pointIndex < points.Length; pointIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var coordinateElement = XmlSupport.Child(points[pointIndex], "coordinates");
            if (coordinateElement is null)
            {
                result.Warnings.Add(new ImportWarning(
                    "MissingCoordinates",
                    "Point缺少coordinates，已跳过该标注点。",
                    name));
                continue;
            }

            var coordinate = ParseCoordinates(coordinateElement.Value, result, name ?? "未命名标注").FirstOrDefault();
            if (coordinate is null)
            {
                continue;
            }

            var waypoint = new Waypoint
            {
                Name = name,
                Description = KmlDescriptionSanitizer.Clean(description),
                Latitude = coordinate.Coordinate.Latitude,
                Longitude = coordinate.Coordinate.Longitude,
                SourceElevation = coordinate.Altitude,
                Timestamp = ReadTimestamp(placemark, result, name),
                Type = WaypointTypeMapper.FromExplicitValues(ExplicitMarkerValues(placemark)
                    .Append(styleUrl?.TrimStart('#')).ToArray()),
                CustomTypeName = BusinessValue(placemark, "hg:customTypeName")
            };
            // 收藏点KMZ的0仅作缺失占位；通过显式元数据恢复真正的可空海拔和原始纯文本。
            if (BusinessValue(placemark, "favoriteId") is not null)
            {
                if (BusinessValue(placemark, "hasElevation") == "false") waypoint.SourceElevation = null;
                if (BusinessValue(placemark, "description") is string favoriteDescription) waypoint.Description = favoriteDescription;
            }
            if (BusinessValue(placemark, "hg:markerType") is string markerType &&
                Enum.TryParse<WaypointType>(markerType, true, out var businessType)) waypoint.Type = businessType;
            if (BusinessValue(placemark, "hg:isGuideNode") is string guideFlag)
                waypoint.OriginalAttributes["hg:isGuideNode"] = guideFlag;
            if (BusinessValue(placemark, "hg:relatedTrackKey") is string relatedKey)
                waypoint.OriginalAttributes["hg:relatedTrackKey"] = relatedKey;
            PreservePlacemarkMetadata(placemark, waypoint.OriginalAttributes, folderPath);
            XmlSupport.PreserveAttributes(
                points[pointIndex],
                waypoint.OriginalAttributes,
                "kml:Point:attribute:");
            foreach (var child in points[pointIndex].Elements().Where(child =>
                         child.Name.LocalName != "coordinates"))
            {
                XmlSupport.PreserveElement(
                    waypoint.OriginalAttributes,
                    $"kml:Point:xml:{child.Name.LocalName}",
                    child);
            }
            result.Waypoints.Add(waypoint);
            foreach (var reference in KmlMediaReferenceExtractor.Extract(placemark))
            {
                if (reference.IsExternal)
                {
                    result.ExternalMediaReferences.Add(new ExternalMediaReference
                    {
                        OwnerType = MediaOwnerType.Waypoint, OwnerId = waypoint.Id, Url = reference.Value,
                        SourceKind = reference.Kind, SourceField = reference.Field,
                        DisplayOrder = reference.DisplayOrder ?? result.MediaAttachments.Count(item => item.OwnerId == waypoint.Id) +
                                       result.ExternalMediaReferences.Count(item => item.OwnerId == waypoint.Id),
                        Caption = reference.Caption
                    });
                }
                else if (embeddedImageResolver is not null)
                {
                    var bytes = embeddedImageResolver(reference.Value, cancellationToken)
                        .GetAwaiter().GetResult();
                    if (bytes is null)
                    {
                        result.Warnings.Add(new ImportWarning("MissingImage", "KMZ图片不存在或路径无效，已跳过。", reference.Value));
                        continue;
                    }
                    if (!ProjectImageInspector.TryInspect(bytes, out var mime, out var extension,
                            out var width, out var height) || _imageValidator is not null && !_imageValidator.CanDecode(bytes))
                    {
                        result.Warnings.Add(new ImportWarning("InvalidImage", "KMZ图片格式无效，已跳过。", reference.Value));
                        continue;
                    }
                    var hash = Convert.ToHexString(System.Security.Cryptography.LegacySha256.HashData(bytes));
                    var blob = result.MediaBlobs.FirstOrDefault(item => item.Sha256 == hash);
                    if (blob is null)
                    {
                        blob = new MediaBlob
                        {
                            Sha256 = hash, MimeType = mime, FileExtension = extension,
                            ByteLength = bytes.LongLength, Width = width, Height = height,
                            PendingBytes = bytes
                        };
                        blob.StoredPath = $"media/originals/{blob.Id:D}{extension}";
                        result.MediaBlobs.Add(blob);
                    }
                    result.MediaAttachments.Add(new MediaAttachment
                    {
                        OwnerType = MediaOwnerType.Waypoint, OwnerId = waypoint.Id, MediaBlobId = blob.Id,
                        OriginalFileName = Path.GetFileName(reference.Value), SourceKind = MediaSourceKind.KmzEmbedded,
                        SourceReference = reference.Value,
                        DisplayOrder = reference.DisplayOrder ?? result.MediaAttachments.Count(item => item.OwnerId == waypoint.Id) +
                                       result.ExternalMediaReferences.Count(item => item.OwnerId == waypoint.Id),
                        Caption = reference.Caption
                    });
                }
            }
        }
    }

    private static IEnumerable<string?> ExplicitMarkerValues(XElement placemark)
    {
        foreach (var data in placemark.Descendants().Where(element =>
                     element.Name.LocalName is "Data" or "SimpleData"))
        {
            var key = data.Attribute("name")?.Value;
            if (key is null || !new[] { "type", "sym", "markerType", "waypointType" }
                    .Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
            yield return XmlSupport.ChildValue(data, "value") ?? data.Value;
        }
    }

    private static string? BusinessValue(XElement placemark, string name) =>
        placemark.Descendants().Where(element => element.Name.LocalName == "Data")
            .FirstOrDefault(element => element.Attribute("name")?.Value == name) is { } data
            ? XmlSupport.ChildValue(data, "value") : null;

    private static KmlCoordinate? ParseGxCoordinate(string text, TrackImportResult result, string context)
    {
        var values = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (values.Length is < 2 or > 3 ||
            !XmlSupport.TryParseDouble(values[0], out var longitude) ||
            !XmlSupport.TryParseDouble(values[1], out var latitude))
        {
            result.Warnings.Add(new ImportWarning("InvalidCoordinate", "gx:coord坐标格式无效，已跳过该点。", context));
            return null;
        }
        var coordinate = new GeoCoordinate(latitude, longitude);
        try { coordinate.Validate(); }
        catch (ArgumentOutOfRangeException)
        {
            result.Warnings.Add(new ImportWarning("InvalidCoordinate", "gx:coord坐标超出有效范围，已跳过该点。", context));
            return null;
        }
        double? altitude = null;
        if (values.Length == 3 && !XmlSupport.TryParseDouble(values[2], out _))
            result.Warnings.Add(new ImportWarning("InvalidElevation", "gx:coord海拔格式无效，已保留为空。", context));
        else if (values.Length == 3 && XmlSupport.TryParseDouble(values[2], out var parsedAltitude)) altitude = parsedAltitude;
        return new KmlCoordinate(coordinate, altitude);
    }

    private static IReadOnlyList<KmlCoordinate> ParseCoordinates(
        string text,
        TrackImportResult result,
        string context)
    {
        var coordinates = new List<KmlCoordinate>();
        var tuples = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        foreach (var tuple in tuples)
        {
            var values = tuple.Split(',');
            if (values.Length < 2 ||
                !XmlSupport.TryParseDouble(values[0], out var longitude) ||
                !XmlSupport.TryParseDouble(values[1], out var latitude))
            {
                result.Warnings.Add(new ImportWarning(
                    "InvalidCoordinate",
                    "KML坐标格式无效，已跳过该点。",
                    context));
                continue;
            }

            var coordinate = new GeoCoordinate(latitude, longitude);
            try
            {
                coordinate.Validate();
            }
            catch (ArgumentOutOfRangeException)
            {
                result.Warnings.Add(new ImportWarning(
                    "InvalidCoordinate",
                    "KML坐标超出有效范围，已跳过该点。",
                    context));
                continue;
            }

            double? altitude = null;
            if (values.Length >= 3 && !string.IsNullOrWhiteSpace(values[2]))
            {
                if (XmlSupport.TryParseDouble(values[2], out var parsedAltitude))
                {
                    altitude = parsedAltitude;
                }
                else
                {
                    result.Warnings.Add(new ImportWarning(
                        "InvalidElevation",
                        "KML海拔格式无效，已保留为空。",
                        context));
                }
            }

            coordinates.Add(new KmlCoordinate(coordinate, altitude));
        }

        return coordinates;
    }

    private static DateTimeOffset? ReadTimestamp(
        XElement placemark,
        TrackImportResult result,
        string? context)
    {
        var timestamp = placemark.Elements().FirstOrDefault(element => element.Name.LocalName == "TimeStamp");
        var value = timestamp is null ? null : XmlSupport.ChildValue(timestamp, "when");
        if (value is null)
        {
            return null;
        }

        if (XmlSupport.TryParseTimestamp(value, out var parsed))
        {
            return parsed;
        }

        result.Warnings.Add(new ImportWarning("InvalidTimestamp", "KML时间格式无效，已保留为空。", context));
        return null;
    }

    private static void PreserveContainerMetadata(XElement container, IDictionary<string, string?> metadata)
    {
        XmlSupport.PreserveAttributes(container, metadata, "kml:Document:attribute:");
        metadata["kml:name"] = XmlSupport.ChildValue(container, "name");
        metadata["kml:description"] = XmlSupport.ReadRichContent(container, "description");
        XmlSupport.PreserveElement(metadata, "kml:ExtendedData", XmlSupport.Child(container, "ExtendedData"));
        foreach (var child in container.Elements().Where(child =>
                     child.Name.LocalName is not (
                         "name" or "description" or "ExtendedData" or "Folder" or "Placemark")))
        {
            XmlSupport.PreserveElement(metadata, $"kml:document:xml:{child.Name.LocalName}", child);
        }
    }

    private static void PreservePlacemarkMetadata(
        XElement placemark,
        IDictionary<string, string?> attributes,
        IReadOnlyList<string> folderPath)
    {
        XmlSupport.PreserveAttributes(placemark, attributes, "kml:Placemark:attribute:");
        if (folderPath.Count > 0)
        {
            attributes["kml:folderPath"] = string.Join("/", folderPath);
        }

        var styleUrl = XmlSupport.ChildValue(placemark, "styleUrl");
        if (styleUrl is not null)
        {
            attributes["kml:styleUrl"] = styleUrl;
        }

        XmlSupport.PreserveElement(attributes, "kml:ExtendedData", XmlSupport.Child(placemark, "ExtendedData"));
        XmlSupport.PreserveElement(attributes, "kml:TimeStamp", XmlSupport.Child(placemark, "TimeStamp"));
        XmlSupport.PreserveElement(attributes, "kml:TimeSpan", XmlSupport.Child(placemark, "TimeSpan"));

        foreach (var child in placemark.Elements().Where(child =>
                     child.Name.LocalName is not (
                         "name" or "description" or "styleUrl" or "ExtendedData" or
                         "TimeStamp" or "TimeSpan" or "Point" or "LineString" or "MultiGeometry") &&
                     !(child.Name.NamespaceName == "http://www.google.com/kml/ext/2.2" &&
                       child.Name.LocalName is "Track" or "MultiTrack")))
        {
            XmlSupport.PreserveElement(attributes, $"kml:xml:{child.Name.LocalName}", child);
        }
    }

    private sealed record KmlCoordinate(GeoCoordinate Coordinate, double? Altitude);
}
