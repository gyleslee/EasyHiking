using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using HikingGuide.Application.Guides;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Projects;
using HikingGuide.Infrastructure.TrackFiles;
using HikingGuide.Infrastructure.Media;
using HikingGuide.Application.Media;
using HikingGuide.Application.TrackFiles;

namespace HikingGuide.Infrastructure.Tests;

public sealed class KmzMediaTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4////fwAJ+wP+fStDAAAAAElFTkSuQmCC");

    [Fact]
    public async Task Kmz_ImportsImagesAndUrl_ThenProjectRemainsIndependentOfSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "HikingGuide-Kmz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var kmz = Path.Combine(root, "图文.kmz");
            using (var archive = ZipFile.Open(kmz, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(archive.CreateEntry("doc.kml").Open(), Encoding.UTF8))
                    writer.Write("<kml><Document><Placemark><name>图文点</name><description><![CDATA[说明 <img src='files/%E5%B8%BD%E5%AD%90.png'> https://example.com/image?id=1]]></description><Point><coordinates>113,24</coordinates></Point></Placemark></Document></kml>");
                using var image = archive.CreateEntry("files/帽子.png").Open();
                image.Write(Png);
            }
            var parsed = await new KmzTrackImporter().ImportAsync(kmz);
            Assert.Single(parsed.Waypoints);
            Assert.Single(parsed.MediaBlobs);
            Assert.Single(parsed.MediaAttachments);
            Assert.Single(parsed.ExternalMediaReferences);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Png)), parsed.MediaBlobs[0].Sha256);
            var project = new HikingProject();
            var session = ProjectImportSession.Create(parsed, kmz, project);
            new ProjectImportService().Commit(session, project, confirmFarNodes: true);
            var path = Path.Combine(root, "project.hikeproj");
            var service = new ProjectFileService();
            await service.SaveAsync(project, path, false);
            File.Delete(kmz);
            var reopened = await service.LoadAsync(path);
            Assert.Single(reopened.MediaBlobs);
            Assert.Single(reopened.MediaAttachments);
            Assert.Single(reopened.ExternalMediaReferences);
            using var saved = ZipFile.OpenRead(path);
            using var input = saved.GetEntry(reopened.MediaBlobs[0].StoredPath)!.Open();
            using var buffer = new MemoryStream();
            await input.CopyToAsync(buffer);
            Assert.Equal(Png, buffer.ToArray());
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("../evil.jpg")]
    [InlineData("C:/evil.jpg")]
    [InlineData("/evil.jpg")]
    [InlineData("files/%2e%2e/evil.jpg")]
    public async Task Kmz_RejectsUnsafeEntries(string path)
    {
        var kmz = Path.Combine(Path.GetTempPath(), "HikingGuide-Kmz-" + Guid.NewGuid().ToString("N") + ".kmz");
        try
        {
            using (var archive = ZipFile.Open(kmz, ZipArchiveMode.Create))
            {
                using (var kml = archive.CreateEntry("doc.kml").Open())
                    kml.Write("<kml/>"u8);
                using var unsafeEntry = archive.CreateEntry(path).Open();
                unsafeEntry.WriteByte(1);
            }
            await Assert.ThrowsAsync<InvalidDataException>(() => new KmzTrackImporter().ImportAsync(kmz));
        }
        finally { File.Delete(kmz); }
    }

    [Fact]
    public async Task Kmz_WithMultipleKmlAndNoDoc_RequiresChoiceRatherThanFirstEntry()
    {
        var kmz = Path.Combine(Path.GetTempPath(), "HikingGuide-Kmz-" + Guid.NewGuid().ToString("N") + ".kmz");
        try
        {
            using (var archive = ZipFile.Open(kmz, ZipArchiveMode.Create))
                foreach (var name in new[] { "a.kml", "b.kml" })
                {
                    using var entry = archive.CreateEntry(name).Open();
                    entry.Write("<kml/>"u8);
                }
            await Assert.ThrowsAsync<InvalidDataException>(() => new KmzTrackImporter().ImportAsync(kmz));
        }
        finally { File.Delete(kmz); }
    }

    [Fact]
    public async Task Kmz_RejectsDuplicateEntryAndCompressionBomb()
    {
        var root = Path.Combine(Path.GetTempPath(), "HikingGuide-Kmz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var duplicate = Path.Combine(root, "duplicate.kmz");
            using (var archive = ZipFile.Open(duplicate, ZipArchiveMode.Create))
            {
                archive.CreateEntry("doc.kml");
                archive.CreateEntry("doc.kml");
            }
            await Assert.ThrowsAsync<InvalidDataException>(() => new KmzTrackImporter().ImportAsync(duplicate));
            var bomb = Path.Combine(root, "bomb.kmz");
            using (var archive = ZipFile.Open(bomb, ZipArchiveMode.Create))
            {
                using (var kml = archive.CreateEntry("doc.kml").Open()) kml.Write("<kml/>"u8);
                using var compressed = archive.CreateEntry("files/zeros.bin").Open();
                compressed.Write(new byte[1024 * 1024]);
            }
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new KmzTrackImporter(new KmzSafetyProfile { MaximumCompressionRatio = 10 }).ImportAsync(bomb));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Kmz_MultipleImageReferences_PreserveOrderAndDeduplicateBlob()
    {
        var kmz = Path.Combine(Path.GetTempPath(), "HikingGuide-Kmz-" + Guid.NewGuid().ToString("N") + ".kmz");
        try
        {
            using (var archive = ZipFile.Open(kmz, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(archive.CreateEntry("doc.kml").Open(), Encoding.UTF8))
                    writer.Write("<kml><Document><Placemark><name>甲</name><description><![CDATA[<img src='files/a.png'> <a href='https://example.com/image?id=1'>图</a> <img src='./files/b.png'>]]></description><Point><coordinates>113,24</coordinates></Point></Placemark><Placemark><name>乙</name><description><![CDATA[<img src='files/a.png'>]]></description><Point><coordinates>113.1,24.1</coordinates></Point></Placemark></Document></kml>");
                foreach (var name in new[] { "files/a.png", "files/b.png" })
                {
                    using var image = archive.CreateEntry(name).Open();
                    image.Write(Png);
                }
            }
            var parsed = await new KmzTrackImporter().ImportAsync(kmz);
            Assert.Equal(2, parsed.Waypoints.Count);
            Assert.Single(parsed.MediaBlobs);
            Assert.Equal(3, parsed.MediaAttachments.Count);
            Assert.Single(parsed.ExternalMediaReferences);
            Assert.Equal(new[] { 0, 2 }, parsed.MediaAttachments.Where(item => item.OwnerId == parsed.Waypoints[0].Id)
                .Select(item => item.DisplayOrder));
            Assert.Equal(1, parsed.ExternalMediaReferences[0].DisplayOrder);
        }
        finally { File.Delete(kmz); }
    }

    [Fact]
    public async Task PlainKml_RecognizesUnextendedNetworkUrlWithoutRequest()
    {
        var path = Path.Combine(Path.GetTempPath(), "HikingGuide-Kml-" + Guid.NewGuid().ToString("N") + ".kml");
        try
        {
            await File.WriteAllTextAsync(path,
                "<kml><Placemark><description><![CDATA[<a href='https://example.com/image?id=1'>图</a> https://example.com/photo]]></description><ExtendedData><Data name='photoUrl'><value>https://example.com/third</value></Data></ExtendedData><Point><coordinates>113,24</coordinates></Point></Placemark></kml>");
            var parsed = await new KmlTrackImporter().ImportAsync(path);
            Assert.Equal(3, parsed.ExternalMediaReferences.Count);
            Assert.Equal(new[] { "https://example.com/image?id=1", "https://example.com/photo", "https://example.com/third" },
                parsed.ExternalMediaReferences.OrderBy(item => item.DisplayOrder).Select(item => item.Url));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Kmz_GxTrackAndMultiTrack_PreserveSegmentsPointsElevationAndTime()
    {
        var path = Path.Combine(Path.GetTempPath(), "HikingGuide-GxTrack-" + Guid.NewGuid().ToString("N") + ".kmz");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("doc.kml").Open(), Encoding.UTF8))
                writer.Write("<kml xmlns='http://www.opengis.net/kml/2.2' xmlns:gx='http://www.google.com/kml/ext/2.2'><Document><Placemark><name>路线</name><gx:MultiTrack><gx:Track><when>2026-08-08T01:00:00Z</when><when>2026-08-08T01:00:01Z</when><gx:coord>113 24 120</gx:coord><gx:coord>113.1 24.1 130</gx:coord></gx:Track><gx:Track><gx:coord>113.2 24.2 140</gx:coord></gx:Track></gx:MultiTrack></Placemark><Placemark><name>图片点</name><description><![CDATA[<img src='files/p.png'>]]></description><Point><coordinates>113,24</coordinates></Point></Placemark></Document></kml>");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
            using (var image = archive.CreateEntry("files/p.png").Open()) image.Write(Png);
            var parsed = await new KmzTrackImporter().ImportAsync(path);
            var track = Assert.Single(parsed.Tracks);
            Assert.Equal(2, track.Segments.Count);
            Assert.Equal(3, track.GetPointCount());
            Assert.Equal(120, track.Segments[0].Points[0].SourceElevation);
            Assert.Equal(DateTimeOffset.Parse("2026-08-08T01:00:01Z"), track.Segments[0].Points[1].Timestamp);
            Assert.Single(parsed.Waypoints);
            Assert.Single(parsed.MediaAttachments);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task DownloadedKmlPreview_IsEmbeddedOnlyWhenImportIsConfirmed()
    {
        var root = Path.Combine(Path.GetTempPath(), "HikingGuide-KmlPreview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cached = Path.Combine(root, "photo.png");
            await File.WriteAllBytesAsync(cached, Png);
            var parsed = new TrackImportResult();
            var waypoint = new Waypoint { Name = "网络图标注", Latitude = 24, Longitude = 113 };
            var reference = new ExternalMediaReference
            {
                OwnerId = waypoint.Id, OwnerType = MediaOwnerType.Waypoint,
                Url = "https://example.com/photo", SourceKind = "HtmlImage"
            };
            parsed.Waypoints.Add(waypoint);
            parsed.ExternalMediaReferences.Add(reference);
            var preview = new HikingProject();
            preview.MediaBlobs.Clear();
            var download = new ExternalMediaDownloadResult(cached, "image/png", ".png", Png.Length,
                Convert.ToHexString(SHA256.HashData(Png)), 1, 1, true);
            var prepared = await new ProjectMediaContentService().PrepareSaveExternalAsync(preview, reference, download);
            parsed.MediaBlobs.Add(prepared.NewBlob!);
            parsed.MediaAttachments.Add(prepared.Attachment);
            var destination = new HikingProject();
            var session = ProjectImportSession.Create(parsed, "source.kml", destination);
            Assert.Empty(destination.MediaAttachments); // Merely previewing must not change the open project.
            session.WaypointSelections.Single().Import = false;
            Assert.Empty(new ProjectImportService().Prepare(session, destination).MediaAttachments);
            session.WaypointSelections.Single().Import = true;
            new ProjectImportService().Commit(session, destination, true);
            Assert.Single(destination.MediaAttachments);
            Assert.Single(destination.MediaBlobs);
            Assert.Equal(Png, destination.MediaBlobs.Single().PendingBytes);
        }
        finally { Directory.Delete(root, true); }
    }
}
