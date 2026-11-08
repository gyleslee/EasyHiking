using System.Globalization;
using System.Xml.Linq;
using HikingGuide.Application.TrackFiles;
using HikingGuide.Core.Models;

namespace HikingGuide.Infrastructure.TrackFiles;

public sealed class GpxTrackExporter : ITrackExporter
{
    private static readonly XNamespace GpxNamespace = "http://www.topografix.com/GPX/1/1";

    public TrackFileFormat Format => TrackFileFormat.Gpx;

    public Task ExportAsync(
        TrackExportRequest request,
        string filePath,
        bool overwrite = false,
        IProgress<TrackOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            progress?.Report(new TrackOperationProgress("正在生成GPX文件……"));
            var root = new XElement(
                GpxNamespace + "gpx",
                new XAttribute("version", "1.1"),
                new XAttribute("creator", HikingGuide.Core.AppProduct.DisplayName));

            var metadata = BuildMetadata(request);
            if (metadata is not null)
            {
                root.Add(metadata);
            }

            foreach (var waypoint in request.Waypoints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                root.Add(BuildWaypoint(waypoint));
            }

            foreach (var track in request.Tracks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                root.Add(BuildTrack(track, cancellationToken));
            }

            var document = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
            await XmlExporterSupport.SaveAtomicallyAsync(document, filePath, overwrite, cancellationToken)
                .ConfigureAwait(false);
        }, cancellationToken);
    }

    private static XElement? BuildMetadata(TrackExportRequest request)
    {
        var name = TrackExportNames.DocumentName(request.Tracks, request.DocumentName ?? Read(request.Metadata, "gpx:name"));
        var description = request.Description ?? Read(request.Metadata, "gpx:desc");
        var extension = XmlExporterSupport.ReadPreservedElement(
            request.Metadata,
            "gpx:metadata:extensions",
            "extensions");
        if (name is null && description is null && extension is null)
        {
            return null;
        }

        var metadata = new XElement(GpxNamespace + "metadata");
        AddValue(metadata, "name", name);
        AddValue(metadata, "desc", description);
        if (extension is not null)
        {
            metadata.Add(extension);
        }

        return metadata;
    }

    private static XElement BuildWaypoint(Waypoint waypoint)
    {
        var element = new XElement(
            GpxNamespace + "wpt",
            new XAttribute("lat", FormatNumber(waypoint.Latitude)),
            new XAttribute("lon", FormatNumber(waypoint.Longitude)));
        XmlSupport.ApplyPreservedAttributes(element, waypoint.OriginalAttributes, "gpx:attribute:");
        AddValue(element, "ele", FormatNumber(waypoint.SourceElevation));
        AddValue(element, "time", FormatTimestamp(waypoint.Timestamp));
        AddValue(element, "name", waypoint.Name);
        AddValue(element, "desc", waypoint.Description);
        AddValue(element, "cmt", Read(waypoint.OriginalAttributes, "gpx:cmt"));
        AddValue(element, "src", Read(waypoint.OriginalAttributes, "gpx:src"));
        AddValue(element, "sym", Read(waypoint.OriginalAttributes, "gpx:sym"));
        AddValue(
            element,
            "type",
            Read(waypoint.OriginalAttributes, "gpx:type") ?? waypoint.Type.ToString());
        AddPreservedGpxContent(element, waypoint.OriginalAttributes);
        return element;
    }

    private static XElement BuildTrack(Track track, CancellationToken cancellationToken)
    {
        var element = new XElement(GpxNamespace + "trk");
        AddValue(element, "name", track.Name);
        AddValue(element, "desc", track.Description);
        AddValue(element, "cmt", Read(track.Metadata, "gpx:cmt"));
        AddValue(element, "src", Read(track.Metadata, "gpx:src"));
        AddValue(element, "type", Read(track.Metadata, "gpx:type"));

        foreach (var segment in ConnectedTrackExportGeometry.Segments(track))
        {
            var segmentElement = new XElement(GpxNamespace + "trkseg");
            XmlSupport.ApplyPreservedAttributes(segmentElement, segment.OriginalAttributes, "gpx:attribute:");
            foreach (var point in segment.Points)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pointElement = new XElement(
                    GpxNamespace + "trkpt",
                    new XAttribute("lat", FormatNumber(point.Latitude)),
                    new XAttribute("lon", FormatNumber(point.Longitude)));
                XmlSupport.ApplyPreservedAttributes(pointElement, point.OriginalAttributes, "gpx:attribute:");
                AddValue(pointElement, "ele", FormatNumber(point.IsCoordinateEdited ? null : point.SourceElevation));
                AddValue(pointElement, "time", FormatTimestamp(point.Timestamp));
                AddValue(pointElement, "name", Read(point.OriginalAttributes, "gpx:name"));
                AddValue(pointElement, "cmt", Read(point.OriginalAttributes, "gpx:cmt"));
                AddValue(pointElement, "desc", Read(point.OriginalAttributes, "gpx:desc"));
                AddValue(pointElement, "src", Read(point.OriginalAttributes, "gpx:src"));
                AddValue(pointElement, "sym", Read(point.OriginalAttributes, "gpx:sym"));
                AddValue(pointElement, "type", Read(point.OriginalAttributes, "gpx:type"));
                AddPreservedGpxContent(pointElement, point.OriginalAttributes);
                segmentElement.Add(pointElement);
            }

            element.Add(segmentElement);
            foreach (var preserved in XmlExporterSupport.ReadPreservedElementsByPrefix(
                         segment.OriginalAttributes,
                         "gpx:xml:"))
            {
                segmentElement.Add(preserved);
            }

            var segmentExtension = XmlExporterSupport.ReadPreservedElement(
                segment.OriginalAttributes,
                "gpx:extensions",
                "extensions");
            if (segmentExtension is not null)
            {
                segmentElement.Add(segmentExtension);
            }
        }

        var extension = XmlExporterSupport.ReadPreservedElement(track.Metadata, "gpx:extensions", "extensions");
        if (extension is not null)
        {
            element.Add(extension);
        }

        return element;
    }

    private static void AddPreservedGpxContent(
        XElement parent,
        IReadOnlyDictionary<string, string?> attributes)
    {
        foreach (var element in XmlExporterSupport.ReadPreservedElementsByPrefix(attributes, "gpx:xml:"))
        {
            parent.Add(element);
        }

        var extension = XmlExporterSupport.ReadPreservedElement(attributes, "gpx:extensions", "extensions");
        if (extension is not null)
        {
            parent.Add(extension);
        }
    }

    private static void AddValue(XElement parent, string localName, string? value)
    {
        if (value is not null)
        {
            parent.Add(new XElement(GpxNamespace + localName, value));
        }
    }

    private static string? Read(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var value) ? value : null;

    private static string FormatNumber(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string? FormatNumber(double? value) =>
        value?.ToString("R", CultureInfo.InvariantCulture);

    private static string? FormatTimestamp(DateTimeOffset? value) =>
        value?.ToString("o", CultureInfo.InvariantCulture);
}
