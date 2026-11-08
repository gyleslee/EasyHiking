using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using HikingGuide.Application.TrackFiles;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;

namespace HikingGuide.Infrastructure.TrackFiles;

public sealed class MergedExportSelection
{
    public HashSet<Guid> TrackIds { get; } = [];
    public HashSet<Guid> MarkerIds { get; } = [];
}

public sealed class ProjectMergedExportService
{
    private static readonly XNamespace Gpx = "http://www.topografix.com/GPX/1/1";
    private static readonly XNamespace Kml = "http://www.opengis.net/kml/2.2";
    private static readonly XNamespace Hg = "https://hikingguide.local/schema/1";

    /// <summary>导出轨迹或逻辑拆分分段，以及确实属于该范围的普通标注、攻略节点和媒体。</summary>
    public async Task ExportTrackAsync(HikingProject project, Track track, SplitSection? section,
        TrackFileFormat format, string filePath, bool overwrite, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(track);
        if (!project.Tracks.Contains(track)) throw new TrackExportException("所选轨迹不属于当前项目。");
        var scope = await Task.Run(() => BuildTrackScope(project, track, section, cancellationToken), cancellationToken);
        await ExportAsync(scope.Project, scope.Selection, format, filePath, overwrite, cancellationToken);
    }

    private static (HikingProject Project, MergedExportSelection Selection) BuildTrackScope(
        HikingProject project, Track track, SplitSection? section, CancellationToken token)
    {
        var chosen = section is null ? null : track.GetSplitSections().FirstOrDefault(item => item.Id == section.Id);
        if (section is not null && (section.TrackId != track.Id || chosen is null))
            throw new TrackExportException("所选拆分分段已变化，请重新选择。");
        var includedSegmentIds = chosen?.GeometryParts.Select(item => item.Id).ToHashSet();
        var scopedTrack = chosen is null ? track : new Track
        {
            Id = track.Id, Name = chosen.Name, DisplayColor = track.DisplayColor,
            Description = chosen.GeometryParts[0].SplitSectionDescription ?? track.Description,
            SourceFile = track.SourceFile, SourceFormat = track.SourceFormat,
            Segments = chosen.GeometryParts.ToList()
        };
        var snapshot = new HikingProject { Name = project.Name };
        snapshot.Tracks.Add(scopedTrack);
        var selection = new MergedExportSelection();
        selection.TrackIds.Add(scopedTrack.Id);
        var snap = chosen is null ? null : new TrackSnapService();
        foreach (var waypoint in project.Waypoints.Where(item => item.AssociatedTrackId == track.Id))
        {
            token.ThrowIfCancellationRequested();
            if (includedSegmentIds is not null &&
                (snap!.Snap(track, waypoint.Coordinate).Position is not { } position ||
                 !includedSegmentIds.Contains(position.TrackSegmentId))) continue;
            snapshot.Waypoints.Add(waypoint);
            selection.MarkerIds.Add(waypoint.Id);
        }
        foreach (var node in project.GuideNodes.Where(item => item.TrackId == track.Id))
        {
            token.ThrowIfCancellationRequested();
            if (includedSegmentIds is not null &&
                (node.ExactPosition is { } exact
                    ? !includedSegmentIds.Contains(exact.TrackSegmentId)
                    : snap!.Snap(track, node.SnappedCoordinate ?? node.OriginalCoordinate).Position is not { } nearest ||
                      !includedSegmentIds.Contains(nearest.TrackSegmentId))) continue;
            snapshot.GuideNodes.Add(node);
            selection.MarkerIds.Add(node.Id);
        }
        foreach (var item in project.MediaBlobs) snapshot.MediaBlobs.Add(item);
        foreach (var item in project.MediaAttachments.Where(item => selection.MarkerIds.Contains(item.OwnerId)))
            snapshot.MediaAttachments.Add(item);
        foreach (var item in project.ExternalMediaReferences.Where(item => selection.MarkerIds.Contains(item.OwnerId)))
            snapshot.ExternalMediaReferences.Add(item);
        return (snapshot, selection);
    }

    public async Task ExportAsync(HikingProject project, MergedExportSelection selection,
        TrackFileFormat format, string filePath, bool overwrite, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(selection);
        if (format is not (TrackFileFormat.Gpx or TrackFileFormat.Kml or TrackFileFormat.Kmz))
            throw new TrackExportException("不支持的合并导出格式。");
        var tracks = project.Tracks.Where(item => selection.TrackIds.Contains(item.Id)).ToArray();
        var waypoints = project.Waypoints.Where(item => selection.MarkerIds.Contains(item.Id)).ToArray();
        var guides = project.GuideNodes.Where(item => selection.MarkerIds.Contains(item.Id)).ToArray();
        if (tracks.Length + waypoints.Length + guides.Length == 0)
            throw new TrackExportException("请至少选择一条轨迹或一个标注。");
        var keys = tracks.Select((track, index) => (track.Id, Key: $"track-{index + 1}"))
            .ToDictionary(item => item.Id, item => item.Key);
        var target = Path.GetFullPath(filePath);
        if (File.Exists(target) && !overwrite)
            throw new TrackExportException("文件已存在，尚未确认替换。");
        var directory = Path.GetDirectoryName(target)!;
        if (!Directory.Exists(directory)) throw new TrackExportException("导出目录不存在。");
        var temp = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await Task.Run(async () =>
            {
                var document = format == TrackFileFormat.Gpx
                    ? BuildGpx(project, tracks, waypoints, guides, keys, cancellationToken)
                    : BuildKml(project, tracks, waypoints, guides, keys, format == TrackFileFormat.Kmz, cancellationToken);
                if (format == TrackFileFormat.Kmz)
                    await WriteKmzAsync(document, project, waypoints, guides, temp, cancellationToken);
                else await SaveXmlAsync(document, temp, cancellationToken);
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (format == TrackFileFormat.Kmz)
            {
                using var archive = ZipFile.OpenRead(temp);
                if (archive.GetEntry("doc.kml") is null) throw new TrackExportException("KMZ验证失败：缺少doc.kml。");
            }
            else await XmlSupport.LoadSecureDocumentAsync(temp, cancellationToken);
            if (File.Exists(target))
            {
                var backup = temp + ".replace";
                try { File.Replace(temp, target, backup, ignoreMetadataErrors: true); }
                finally { if (File.Exists(backup)) File.Delete(backup); }
            }
            else File.Move(temp, target);
        }
        catch (OperationCanceledException) { throw; }
        catch (TrackExportException) { throw; }
        catch (Exception exception)
        { throw new TrackExportException("合并导出失败。原文件未被修改，请检查磁盘空间和文件权限。", exception); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static XDocument BuildGpx(HikingProject project, Track[] tracks, Waypoint[] waypoints,
        GuideNode[] guides, Dictionary<Guid, string> keys, CancellationToken token)
    {
        var root = new XElement(Gpx + "gpx", new XAttribute("version", "1.1"),
            new XAttribute("creator", HikingGuide.Core.AppProduct.DisplayName), new XAttribute(XNamespace.Xmlns + "hg", Hg));
        root.Add(new XElement(Gpx + "metadata", new XElement(Gpx + "name", TrackExportNames.DocumentName(tracks, project.Name))));
        foreach (var waypoint in waypoints)
            root.Add(GpxMarker(project, waypoint, false,
                waypoint.AssociatedTrackId is Guid id && keys.TryGetValue(id, out var key) ? key : null));
        foreach (var node in guides)
            root.Add(GpxMarker(project, node.ToWaypoint(), true, node.TrackId is Guid id && keys.TryGetValue(id, out var key) ? key : null));
        foreach (var track in tracks)
        {
            token.ThrowIfCancellationRequested();
            var element = new XElement(Gpx + "trk", new XElement(Gpx + "name", track.Name));
            if (track.Description is not null) element.Add(new XElement(Gpx + "desc", track.Description));
            element.Add(new XElement(Gpx + "extensions", new XElement(Hg + "trackKey", keys[track.Id])));
            foreach (var segment in ConnectedTrackExportGeometry.Segments(track))
            {
                var trkseg = new XElement(Gpx + "trkseg");
                foreach (var point in segment.Points)
                {
                    token.ThrowIfCancellationRequested();
                    var trkpt = new XElement(Gpx + "trkpt", new XAttribute("lat", Number(point.Latitude)),
                        new XAttribute("lon", Number(point.Longitude)));
                    if (!point.IsCoordinateEdited && point.SourceElevation is double elevation)
                        trkpt.Add(new XElement(Gpx + "ele", Number(elevation)));
                    if (point.Timestamp is DateTimeOffset time) trkpt.Add(new XElement(Gpx + "time", time.ToString("o")));
                    trkseg.Add(trkpt);
                }
                element.Add(trkseg);
            }
            root.Add(element);
        }
        return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
    }

    private static XElement GpxMarker(HikingProject project, Waypoint waypoint, bool isGuide, string? trackKey)
    {
        var element = new XElement(Gpx + "wpt", new XAttribute("lat", Number(waypoint.Latitude)),
            new XAttribute("lon", Number(waypoint.Longitude)), new XElement(Gpx + "name", waypoint.Name));
        if (waypoint.Description is not null) element.Add(new XElement(Gpx + "desc", waypoint.Description));
        if (waypoint.SourceElevation is double elevation) element.Add(new XElement(Gpx + "ele", Number(elevation)));
        element.Add(new XElement(Gpx + "type", waypoint.Type.ToString()));
        var extension = new XElement(Gpx + "extensions", new XElement(Hg + "isGuideNode", isGuide),
            new XElement(Hg + "markerType", waypoint.Type.ToString()));
        if (waypoint.CustomTypeName is not null) extension.Add(new XElement(Hg + "customTypeName", waypoint.CustomTypeName));
        if (trackKey is not null) extension.Add(new XElement(Hg + "relatedTrackKey", trackKey));
        foreach (var external in project.ExternalMediaReferences.Where(item => item.OwnerId == waypoint.Id)
                     .OrderBy(item => item.DisplayOrder))
            extension.Add(new XElement(Hg + "externalMedia", new XAttribute("order", external.DisplayOrder),
                new XAttribute("caption", external.Caption ?? ""), external.Url));
        element.Add(extension);
        return element;
    }

    private static XDocument BuildKml(HikingProject project, Track[] tracks, Waypoint[] waypoints,
        GuideNode[] guides, Dictionary<Guid, string> keys, bool includeImages, CancellationToken token)
    {
        var document = new XElement(Kml + "Document", new XElement(Kml + "name", TrackExportNames.DocumentName(tracks, project.Name)));
        var trackFolder = new XElement(Kml + "Folder", new XElement(Kml + "name", tracks.Length == 1 ? TrackExportNames.DocumentName(tracks, project.Name) : "轨迹"));
        foreach (var track in tracks)
        {
            token.ThrowIfCancellationRequested();
            var placemark = new XElement(Kml + "Placemark", new XElement(Kml + "name", track.Name),
                KmlData("hg:trackKey", keys[track.Id]));
            if (track.Description is not null)
                placemark.Add(new XElement(Kml + "description", KmlDescriptionSanitizer.Clean(track.Description)));
            var geometries = ConnectedTrackExportGeometry.Segments(track).Select(segment =>
            {
                var coordinates = string.Join(' ', segment.Points.Select(point =>
                    Number(point.Longitude) + "," + Number(point.Latitude) +
                    (!point.IsCoordinateEdited && point.SourceElevation is double elevation ? "," + Number(elevation) : "")));
                return new XElement(Kml + "LineString", new XElement(Kml + "coordinates", coordinates));
            }).ToArray();
            if (geometries.Length == 1) placemark.Add(geometries[0]);
            else placemark.Add(new XElement(Kml + "MultiGeometry", geometries));
            trackFolder.Add(placemark);
        }
        document.Add(trackFolder);
        var guideFolder = new XElement(Kml + "Folder", new XElement(Kml + "name", "攻略节点"));
        foreach (var node in guides)
            guideFolder.Add(KmlMarker(project, node.ToWaypoint(), true,
                node.TrackId is Guid id && keys.TryGetValue(id, out var key) ? key : null, includeImages));
        document.Add(guideFolder);
        var waypointFolder = new XElement(Kml + "Folder", new XElement(Kml + "name", "普通标注"));
        foreach (var waypoint in waypoints)
            waypointFolder.Add(KmlMarker(project, waypoint, false,
                waypoint.AssociatedTrackId is Guid id && keys.TryGetValue(id, out var key) ? key : null,
                includeImages));
        document.Add(waypointFolder);
        return new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement(Kml + "kml", document));
    }

    private static XElement KmlMarker(HikingProject project, Waypoint waypoint, bool isGuide,
        string? trackKey, bool includeImages)
    {
        var marker = new XElement(Kml + "Placemark", new XElement(Kml + "name", waypoint.Name));
        var plainDescription = KmlDescriptionSanitizer.Clean(waypoint.Description);
        var html = KmlMediaDescriptionBuilder.PlainText(plainDescription);
        var hasImage = false;
        var attachments = project.MediaAttachments.Where(item => item.OwnerId == waypoint.Id)
            .OrderBy(item => item.DisplayOrder).ToArray();
        if (includeImages)
            foreach (var attachment in attachments)
            {
                hasImage = true;
                var blob = project.MediaBlobs.First(item => item.Id == attachment.MediaBlobId);
                KmlMediaDescriptionBuilder.Image(html, "files/" + blob.Id.ToString("D") + blob.FileExtension);
            }
        foreach (var url in ExternalImageUrls(project, waypoint.Id))
        {
            // 已下载的同一张图优先使用 KMZ 内部原图，避免在手机上显示两次。
            if (includeImages && attachments.Any(item => string.Equals(item.SourceReference, url,
                    StringComparison.Ordinal))) continue;
            hasImage = true;
            KmlMediaDescriptionBuilder.Image(html, url);
        }
        if (hasImage) marker.Add(KmlMediaDescriptionBuilder.Description(Kml, html));
        else if (plainDescription.Length > 0)
            marker.Add(new XElement(Kml + "description", plainDescription));
        var data = KmlData("hg:isGuideNode", isGuide ? "true" : "false");
        data.Add(new XElement(Kml + "Data", new XAttribute("name", "hg:markerType"),
            new XElement(Kml + "value", waypoint.Type.ToString())));
        if (waypoint.CustomTypeName is not null)
            data.Add(new XElement(Kml + "Data", new XAttribute("name", "hg:customTypeName"),
                new XElement(Kml + "value", waypoint.CustomTypeName)));
        if (trackKey is not null)
            data.Add(new XElement(Kml + "Data", new XAttribute("name", "hg:relatedTrackKey"),
                new XElement(Kml + "value", trackKey)));
        var externalIndex = 0;
        foreach (var url in ExternalImageUrls(project, waypoint.Id))
            data.Add(new XElement(Kml + "Data", new XAttribute("name", $"hg:externalMedia:{externalIndex++}"),
                new XElement(Kml + "value", url)));
        marker.Add(data);
        var point = new XElement(Kml + "Point", new XElement(Kml + "coordinates",
            Number(waypoint.Longitude) + "," + Number(waypoint.Latitude) +
            (waypoint.SourceElevation is double elevation ? "," + Number(elevation) : "")));
        marker.Add(point);
        return marker;
    }

    private static XElement KmlData(string name, string value) =>
        new(Kml + "ExtendedData", new XElement(Kml + "Data", new XAttribute("name", name),
            new XElement(Kml + "value", value)));

    private static IEnumerable<string> ExternalImageUrls(HikingProject project, Guid ownerId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in project.ExternalMediaReferences.Where(item => item.OwnerId == ownerId)
                     .OrderBy(item => item.DisplayOrder))
            if (IsHttpUrl(item.Url) && seen.Add(item.Url)) yield return item.Url;
        foreach (var item in project.MediaAttachments.Where(item => item.OwnerId == ownerId)
                     .OrderBy(item => item.DisplayOrder))
            if (item.SourceReference is { } url && IsHttpUrl(url) && seen.Add(url)) yield return url;
    }

    private static bool IsHttpUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
        parsed.Scheme is "http" or "https";

    private static async Task WriteKmzAsync(XDocument document, HikingProject project,
        Waypoint[] waypoints, GuideNode[] guides, string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, true);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        var kmlEntry = archive.CreateEntry("doc.kml", CompressionLevel.Optimal);
        await using (var output = kmlEntry.Open()) await WriteXmlAsync(document, output, token);
        var ownerIds = waypoints.Select(item => item.Id).Concat(guides.Select(item => item.Id)).ToHashSet();
        var blobIds = project.MediaAttachments.Where(item => ownerIds.Contains(item.OwnerId))
            .Select(item => item.MediaBlobId).Distinct();
        foreach (var id in blobIds)
        {
            token.ThrowIfCancellationRequested();
            var blob = project.MediaBlobs.First(item => item.Id == id);
            var output = archive.CreateEntry($"files/{blob.Id:D}{blob.FileExtension}", CompressionLevel.Optimal);
            await using var destination = output.Open();
            await using var original = await OpenOriginalAsync(blob, token);
            await original.CopyToAsync(destination, token);
        }
    }

    private static async Task<Stream> OpenOriginalAsync(MediaBlob blob, CancellationToken token)
    {
        if (blob.PendingBytes is { } bytes)
        {
            if (!Convert.ToHexString(System.Security.Cryptography.LegacySha256.HashData(bytes)).Equals(blob.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new TrackExportException("项目图片SHA-256校验失败。");
            return new MemoryStream(bytes, writable: false);
        }
        if (blob.ArchiveFilePath is null) throw new TrackExportException("项目图片原图来源不存在。");
        using var archive = ZipFile.OpenRead(blob.ArchiveFilePath);
        var entry = archive.GetEntry(blob.StoredPath) ?? throw new TrackExportException("项目图片原图缺失。");
        await using var input = entry.Open();
        var output = new MemoryStream();
        await input.CopyToAsync(output, token);
        if (!Convert.ToHexString(System.Security.Cryptography.LegacySha256.HashData(output.ToArray())).Equals(blob.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new TrackExportException("项目图片SHA-256校验失败。");
        output.Position = 0;
        return output;
    }

    private static async Task SaveXmlAsync(XDocument document, string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        await WriteXmlAsync(document, stream, token);
    }
    private static async Task WriteXmlAsync(XDocument document, Stream stream, CancellationToken token)
    {
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Async = true, Encoding = new UTF8Encoding(false), Indent = true });
        await document.SaveAsync(writer, token);
        await writer.FlushAsync();
    }
    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
