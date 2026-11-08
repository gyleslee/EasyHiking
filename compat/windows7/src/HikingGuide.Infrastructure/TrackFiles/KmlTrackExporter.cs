using System.Globalization;
using System.Text;
using System.Xml.Linq;
using HikingGuide.Application.TrackFiles;
using HikingGuide.Core.Models;

namespace HikingGuide.Infrastructure.TrackFiles;

public sealed class KmlTrackExporter : ITrackExporter
{
    private static readonly XNamespace KmlNamespace = "http://www.opengis.net/kml/2.2";

    public TrackFileFormat Format => TrackFileFormat.Kml;

    public Task ExportAsync(
        TrackExportRequest request,
        string filePath,
        bool overwrite = false,
        IProgress<TrackOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            progress?.Report(new TrackOperationProgress("正在生成KML文件……"));
            var documentElement = new XElement(KmlNamespace + "Document");
            XmlSupport.ApplyPreservedAttributes(
                documentElement,
                request.Metadata,
                "kml:Document:attribute:");
            AddValue(documentElement, "name", TrackExportNames.DocumentName(request.Tracks, request.DocumentName ?? Read(request.Metadata, "kml:name")));
            AddValue(documentElement, "description", request.Description ?? Read(request.Metadata, "kml:description"));
            var documentExtendedData = XmlExporterSupport.ReadPreservedElement(
                request.Metadata,
                "kml:ExtendedData",
                "ExtendedData");
            if (documentExtendedData is not null)
            {
                documentElement.Add(documentExtendedData);
            }

            foreach (var element in XmlExporterSupport.ReadPreservedElementsByPrefix(
                         request.Metadata,
                         "kml:document:xml:"))
            {
                documentElement.Add(element);
            }

            foreach (var track in request.Tracks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                documentElement.Add(BuildTrack(track, cancellationToken));
            }

            foreach (var waypoint in request.Waypoints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                documentElement.Add(BuildWaypoint(waypoint));
            }

            var document = new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement(KmlNamespace + "kml", documentElement));
            await XmlExporterSupport.SaveAtomicallyAsync(document, filePath, overwrite, cancellationToken)
                .ConfigureAwait(false);
        }, cancellationToken);
    }

    private static XElement BuildTrack(Track track, CancellationToken cancellationToken)
    {
        var placemark = new XElement(KmlNamespace + "Placemark");
        XmlSupport.ApplyPreservedAttributes(placemark, track.Metadata, "kml:Placemark:attribute:");
        AddValue(placemark, "name", track.Name);
        AddValue(placemark, "description", track.Description);
        AddPreservedPlacemarkContent(placemark, track.Metadata, includeTimeStamp: true);

        var exportSegments = ConnectedTrackExportGeometry.Segments(track);
        if (exportSegments.Count == 1)
        {
            placemark.Add(BuildLineString(exportSegments[0], cancellationToken));
        }
        else if (exportSegments.Count > 1)
        {
            var multiGeometry = new XElement(KmlNamespace + "MultiGeometry");
            foreach (var segment in exportSegments)
            {
                multiGeometry.Add(BuildLineString(segment, cancellationToken));
            }

            placemark.Add(multiGeometry);
        }

        return placemark;
    }

    private static XElement BuildLineString(TrackSegment segment, CancellationToken cancellationToken)
    {
        var coordinateText = new StringBuilder(Math.Max(64, segment.Points.Count * 32));
        foreach (var point in segment.Points)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (coordinateText.Length > 0)
            {
                coordinateText.Append(' ');
            }

            coordinateText.Append(FormatNumber(point.Longitude));
            coordinateText.Append(',');
            coordinateText.Append(FormatNumber(point.Latitude));
            if (!point.IsCoordinateEdited && point.SourceElevation.HasValue)
            {
                coordinateText.Append(',');
                coordinateText.Append(FormatNumber(point.SourceElevation.Value));
            }
        }

        var lineString = new XElement(
            KmlNamespace + "LineString",
            new XElement(KmlNamespace + "coordinates", coordinateText.ToString()));
        XmlSupport.ApplyPreservedAttributes(
            lineString,
            segment.OriginalAttributes,
            "kml:LineString:attribute:");
        var coordinates = lineString.Element(KmlNamespace + "coordinates");
        foreach (var element in XmlExporterSupport.ReadPreservedElementsByPrefix(
                     segment.OriginalAttributes,
                     "kml:LineString:xml:"))
        {
            coordinates?.AddBeforeSelf(element);
        }

        return lineString;
    }

    private static XElement BuildWaypoint(Waypoint waypoint)
    {
        var placemark = new XElement(KmlNamespace + "Placemark");
        XmlSupport.ApplyPreservedAttributes(placemark, waypoint.OriginalAttributes, "kml:Placemark:attribute:");
        AddValue(placemark, "name", waypoint.Name);
        AddValue(placemark, "description", waypoint.Description);
        AddPreservedPlacemarkContent(
            placemark,
            waypoint.OriginalAttributes,
            includeTimeStamp: !waypoint.Timestamp.HasValue);
        if (waypoint.Timestamp.HasValue)
        {
            placemark.Add(
                new XElement(
                    KmlNamespace + "TimeStamp",
                    new XElement(
                        KmlNamespace + "when",
                        waypoint.Timestamp.Value.ToString("o", CultureInfo.InvariantCulture))));
        }

        var coordinate = new StringBuilder()
            .Append(FormatNumber(waypoint.Longitude))
            .Append(',')
            .Append(FormatNumber(waypoint.Latitude));
        if (waypoint.SourceElevation.HasValue)
        {
            coordinate.Append(',').Append(FormatNumber(waypoint.SourceElevation.Value));
        }

        var point = new XElement(
            KmlNamespace + "Point",
            new XElement(KmlNamespace + "coordinates", coordinate.ToString()));
        XmlSupport.ApplyPreservedAttributes(point, waypoint.OriginalAttributes, "kml:Point:attribute:");
        var coordinatesElement = point.Element(KmlNamespace + "coordinates");
        foreach (var element in XmlExporterSupport.ReadPreservedElementsByPrefix(
                     waypoint.OriginalAttributes,
                     "kml:Point:xml:"))
        {
            coordinatesElement?.AddBeforeSelf(element);
        }

        placemark.Add(point);
        return placemark;
    }

    private static void AddPreservedPlacemarkContent(
        XElement placemark,
        IReadOnlyDictionary<string, string?> attributes,
        bool includeTimeStamp)
    {
        AddValue(placemark, "styleUrl", Read(attributes, "kml:styleUrl"));
        var extendedData = XmlExporterSupport.ReadPreservedElement(attributes, "kml:ExtendedData", "ExtendedData");
        if (extendedData is not null)
        {
            placemark.Add(extendedData);
        }

        if (includeTimeStamp)
        {
            var timestamp = XmlExporterSupport.ReadPreservedElement(attributes, "kml:TimeStamp", "TimeStamp");
            if (timestamp is not null)
            {
                placemark.Add(timestamp);
            }
        }

        var timeSpan = XmlExporterSupport.ReadPreservedElement(attributes, "kml:TimeSpan", "TimeSpan");
        if (timeSpan is not null)
        {
            placemark.Add(timeSpan);
        }

        foreach (var element in XmlExporterSupport.ReadPreservedElementsByPrefix(attributes, "kml:xml:")
                     .Where(element => element.Name.LocalName is not
                         ("Track" or "MultiTrack" or "LineString" or "MultiGeometry") ||
                         element.Name.NamespaceName is not
                         ("http://www.google.com/kml/ext/2.2" or "http://www.opengis.net/kml/2.2")))
        {
            placemark.Add(element);
        }
    }

    private static void AddValue(XElement parent, string localName, string? value)
    {
        if (value is not null)
        {
            parent.Add(new XElement(KmlNamespace + localName, value));
        }
    }

    private static string? Read(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var value) ? value : null;

    private static string FormatNumber(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
