using System.IO.Compression;
using System.Security.Cryptography;
using HikingGuide.Application.Guides;
using HikingGuide.Application.TrackFiles;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.TrackFiles;

namespace HikingGuide.Infrastructure.Tests;

public sealed class MergedExportTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4////fwAJ+wP+fStDAAAAAElFTkSuQmCC");

    [Theory]
    [InlineData(TrackFileFormat.Gpx)]
    [InlineData(TrackFileFormat.Kml)]
    [InlineData(TrackFileFormat.Kmz)]
    public async Task Merge_ExportsDistinctTracksMarkersAndExternalReferences(TrackFileFormat format)
    {
        var root = Path.Combine(Path.GetTempPath(), "HikingGuide-Merged-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = CreateProject();
            var selected = new MergedExportSelection();
            foreach (var track in project.Tracks) selected.TrackIds.Add(track.Id);
            foreach (var waypoint in project.Waypoints) selected.MarkerIds.Add(waypoint.Id);
            foreach (var node in project.GuideNodes) selected.MarkerIds.Add(node.Id);
            var path = Path.Combine(root, "merged." + format.ToString().ToLowerInvariant());
            await new ProjectMergedExportService().ExportAsync(project, selected, format, path, false);
            Assert.False(project.IsDirty);
            TrackImportResult parsed = format switch
            {
                TrackFileFormat.Gpx => await new GpxTrackImporter().ImportAsync(path),
                TrackFileFormat.Kml => await new KmlTrackImporter().ImportAsync(path),
                _ => await new KmzTrackImporter().ImportAsync(path)
            };
            Assert.Equal(2, parsed.Tracks.Count);
            Assert.Equal(new[] { 2, 1 }, parsed.Tracks.Select(item => item.Segments.Count));
            Assert.Equal(3, parsed.TrackPointCount);
            Assert.Equal(2, parsed.Waypoints.Count);
            Assert.Single(parsed.ExternalMediaReferences);
            var guide = Assert.Single(parsed.Waypoints, item => item.Name == "山顶");
            Assert.Equal("true", guide.OriginalAttributes["hg:isGuideNode"]);
            Assert.Equal(WaypointType.Custom, guide.Type);
            Assert.Equal("观景峰", guide.CustomTypeName);
            if (format == TrackFileFormat.Kmz)
            {
                Assert.Single(parsed.MediaBlobs);
                Assert.Equal(2, parsed.MediaAttachments.Count);
                Assert.Equal(Convert.ToHexString(SHA256.HashData(Png)), parsed.MediaBlobs[0].Sha256);
                using var archive = ZipFile.OpenRead(path);
                Assert.Equal(2, archive.Entries.Count);
                var newProject = new HikingProject();
                var session = ProjectImportSession.Create(parsed, path, newProject);
                session.SelectedGuideTrackId = parsed.Tracks[0].Id;
                new ProjectImportService().Commit(session, newProject, true);
                Assert.Single(newProject.GuideNodes);
                Assert.Equal(2, newProject.MediaAttachments.Count);
                Assert.Single(newProject.MediaBlobs);
            }
            else Assert.Empty(parsed.MediaBlobs);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MarkerOnlyKmz_ImportsAsOrdinaryMarkerWhenNoMainTrackExists()
    {
        var root = Path.Combine(Path.GetTempPath(), "HikingGuide-Merged-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = CreateProject();
            var selected = new MergedExportSelection();
            selected.MarkerIds.Add(project.GuideNodes.Single().Id);
            var path = Path.Combine(root, "guide-only.kmz");
            await new ProjectMergedExportService().ExportAsync(project, selected, TrackFileFormat.Kmz, path, false);
            var parsed = await new KmzTrackImporter().ImportAsync(path);
            Assert.Empty(parsed.Tracks);
            Assert.Single(parsed.Waypoints);
            var reopened = new HikingProject();
            var session = ProjectImportSession.Create(parsed, path, reopened);
            session.SetAllGuide(false);
            new ProjectImportService().Commit(session, reopened, true);
            Assert.Empty(reopened.GuideNodes);
            Assert.Single(reopened.Waypoints);
            Assert.Single(reopened.MediaAttachments);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task EmptySelectionAndUserHtml_AreHandledSafely()
    {
        var root = Path.Combine(Path.GetTempPath(), "HikingGuide-Merged-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = CreateProject();
            var service = new ProjectMergedExportService();
            var path = Path.Combine(root, "safe.kml");
            await Assert.ThrowsAsync<TrackExportException>(() =>
                service.ExportAsync(project, new MergedExportSelection(), TrackFileFormat.Kml, path, false));
            project.Waypoints[0].Description = "<script>alert(1)</script>";
            var selected = new MergedExportSelection();
            selected.MarkerIds.Add(project.Waypoints[0].Id);
            await service.ExportAsync(project, selected, TrackFileFormat.Kml, path, false);
            var xml = await File.ReadAllTextAsync(path);
            Assert.Contains("&lt;script&gt;", xml);
            Assert.DoesNotContain("<script>", xml);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task KmzDescription_UsesCDataAndRelativeImages_WithoutLegacyEscapes()
    {
        var root = Path.Combine(Path.GetTempPath(), "HikingGuide-P82-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = CreateProject();
            var waypoint = project.Waypoints[0];
            waypoint.Description = "茅坪坳凉亭\n&lt;div&gt;&lt;a href=&quot;&quot;&gt;&lt;img src=&quot;https://example.com/old.jpg&quot;&gt;&lt;/a&gt;&lt;/div&gt;\n注意：A & B，A < B，\"测试\"";
            var selection = new MergedExportSelection();
            selection.MarkerIds.Add(waypoint.Id);
            var path = Path.Combine(root, "images.kmz");
            await new ProjectMergedExportService().ExportAsync(project, selection, TrackFileFormat.Kmz, path, false);
            using var archive = ZipFile.OpenRead(path);
            var entry = Assert.IsType<ZipArchiveEntry>(archive.GetEntry("doc.kml"));
            using var reader = new StreamReader(entry.Open());
            var xml = await reader.ReadToEndAsync();
            Assert.Contains("<![CDATA[", xml);
            Assert.Contains("<img src=\"files/", xml);
            Assert.DoesNotContain("&lt;img", xml);
            Assert.DoesNotContain("href=\"\"", xml);
            Assert.DoesNotContain("old.jpg", xml);
            Assert.Contains("茅坪坳凉亭", xml);
            Assert.Contains("注意：A &amp; B", xml);
            Assert.NotNull(archive.GetEntry("files/" + project.MediaBlobs[0].Id.ToString("D") + ".png"));
            var imported = await new KmzTrackImporter().ImportAsync(path);
            Assert.Single(imported.MediaAttachments);
            Assert.Equal("茅坪坳凉亭\n注意：A & B，A < B，\"测试\"", imported.Waypoints[0].Description);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task KmzRoundTrip_ThreeGenerations_DoNotAccumulateMediaMarkup()
    {
        var root = Path.Combine(Path.GetTempPath(), "HikingGuide-P82-RoundTrip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = CreateProject();
            var marker = project.Waypoints[0];
            marker.Description = "营地有水源，A & B";
            var expectedHash = project.MediaBlobs[0].Sha256;
            for (var cycle = 0; cycle < 3; cycle++)
            {
                var path = Path.Combine(root, $"roundtrip-{cycle}.kmz");
                var selected = new MergedExportSelection();
                selected.MarkerIds.Add(marker.Id);
                await new ProjectMergedExportService().ExportAsync(project, selected, TrackFileFormat.Kmz, path, false);
                var parsed = await new KmzTrackImporter().ImportAsync(path);
                marker = Assert.Single(parsed.Waypoints);
                Assert.Equal("营地有水源，A & B", marker.Description);
                Assert.Single(parsed.MediaAttachments);
                Assert.Equal(expectedHash, Assert.Single(parsed.MediaBlobs).Sha256);
                project = new HikingProject();
                var session = ProjectImportSession.Create(parsed, path, project);
                new ProjectImportService().Commit(session, project, true);
                marker = Assert.Single(project.Waypoints);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task KmlExternalImage_RoundTripsPlainDescriptionWithoutEscapedImageText()
    {
        var root = Path.Combine(Path.GetTempPath(), "HikingGuide-P82-Kml-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = CreateProject();
            var guide = project.GuideNodes[0];
            guide.Description = "山顶风大，A < B & \"注意\"";
            var selected = new MergedExportSelection();
            selected.MarkerIds.Add(guide.Id);
            var path = Path.Combine(root, "external.kml");
            await new ProjectMergedExportService().ExportAsync(project, selected, TrackFileFormat.Kml, path, false);
            var xml = await File.ReadAllTextAsync(path);
            Assert.Contains("<img src=\"https://example.com/image?id=1\"/>", xml);
            Assert.DoesNotContain("&lt;img", xml);
            Assert.DoesNotContain("file:///", xml);
            var parsed = await new KmlTrackImporter().ImportAsync(path);
            var marker = Assert.Single(parsed.Waypoints);
            Assert.Equal("山顶风大，A < B & \"注意\"", marker.Description);
            Assert.Single(parsed.ExternalMediaReferences);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task KmlMediaDescription_WithCDataTerminator_RemainsWellFormed()
    {
        var root = Path.Combine(Path.GetTempPath(), "HikingGuide-P82-CData-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = CreateProject();
            var guide = project.GuideNodes[0];
            guide.Description = "说明 ]]> 后续";
            var selected = new MergedExportSelection();
            selected.MarkerIds.Add(guide.Id);
            var path = Path.Combine(root, "cdata.kml");
            await new ProjectMergedExportService().ExportAsync(project, selected, TrackFileFormat.Kml, path, false);
            var parsed = await new KmlTrackImporter().ImportAsync(path);
            Assert.Equal("说明 ]]> 后续", Assert.Single(parsed.Waypoints).Description);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task KmzOneMarkerWithTwoPictures_PreservesBothImages()
    {
        var root = Path.Combine(Path.GetTempPath(), "HikingGuide-P82-MultiImage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = CreateProject();
            var marker = project.Waypoints[0];
            var secondBlob = new MediaBlob
            {
                Sha256 = project.MediaBlobs[0].Sha256, MimeType = "image/png",
                FileExtension = ".png", ByteLength = Png.Length, Width = 1, Height = 1,
                PendingBytes = Png
            };
            project.MediaBlobs.Add(secondBlob);
            project.MediaAttachments.Add(new MediaAttachment
            {
                OwnerType = MediaOwnerType.Waypoint, OwnerId = marker.Id,
                MediaBlobId = secondBlob.Id, DisplayOrder = 1
            });
            var selected = new MergedExportSelection();
            selected.MarkerIds.Add(marker.Id);
            var path = Path.Combine(root, "two-images.kmz");
            await new ProjectMergedExportService().ExportAsync(project, selected, TrackFileFormat.Kmz, path, false);
            using var archive = ZipFile.OpenRead(path);
            Assert.Equal(3, archive.Entries.Count);
            using var reader = new StreamReader(archive.GetEntry("doc.kml")!.Open());
            var xml = await reader.ReadToEndAsync();
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(xml, "<img src=\"files/").Count);
            var parsed = await new KmzTrackImporter().ImportAsync(path);
            Assert.Equal(2, parsed.MediaAttachments.Count);
            Assert.Single(parsed.MediaBlobs);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task TrackMenuKmz_IncludesGuideNodesAndTheirEmbeddedPictures()
    {
        var root = Path.Combine(Path.GetTempPath(), "HikingGuide-TrackKmz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = CreateProject();
            var track = project.Tracks[0];
            var guide = project.GuideNodes[0];
            guide.Description = "<div><a href=\"\" target=\"_blank\"><img src=\"https://example.com/image?id=1\"/>合法营地</a></div>";
            project.MediaAttachments[1].SourceReference = "https://example.com/image?id=1";
            var path = Path.Combine(root, "track.kmz");
            await new ProjectMergedExportService().ExportTrackAsync(project, track, null,
                TrackFileFormat.Kmz, path, false);
            using var archive = ZipFile.OpenRead(path);
            Assert.Equal(2, archive.Entries.Count);
            using var reader = new StreamReader(archive.GetEntry("doc.kml")!.Open());
            var xml = await reader.ReadToEndAsync();
            Assert.Contains("<img src=\"files/", xml);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(xml, "<img src="));
            Assert.DoesNotContain("&lt;div", xml);
            Assert.DoesNotContain("href=", xml);
            var imported = await new KmzTrackImporter().ImportAsync(path);
            Assert.Single(imported.Tracks);
            Assert.Single(imported.Waypoints);
            Assert.Equal("合法营地", imported.Waypoints[0].Description);
            Assert.Single(imported.MediaAttachments);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task TrackMenuKmz_PreservesOrdinaryMarkerTrackRelationship()
    {
        var root = Path.Combine(Path.GetTempPath(), "HikingGuide-TrackMarker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = CreateProject();
            var track = project.Tracks[0];
            project.Waypoints[0].AssociatedTrackId = track.Id;
            var path = Path.Combine(root, "track.kmz");
            await new ProjectMergedExportService().ExportTrackAsync(project, track, null,
                TrackFileFormat.Kmz, path, false);
            var parsed = await new KmzTrackImporter().ImportAsync(path);
            Assert.Equal(2, parsed.Waypoints.Count);
            var ordinary = Assert.Single(parsed.Waypoints, item => item.Name == "普通点");
            Assert.Equal("track-1", ordinary.OriginalAttributes["hg:relatedTrackKey"]);
            Assert.Equal(2, parsed.MediaAttachments.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SplitSectionKmz_ExportsOnlySectionGeometryAndRelatedMarkers()
    {
        var root = Path.Combine(Path.GetTempPath(), "HikingGuide-SectionKmz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = CreateProject();
            var track = project.Tracks[0];
            var second = new TrackSegment { SplitSectionName = "第二分段" };
            second.Points.Add(new TrackPoint { Latitude = 24.002, Longitude = 113.002 });
            second.Points.Add(new TrackPoint { Latitude = 24.003, Longitude = 113.003 });
            track.Segments.Add(second);
            track.RecalculateDistances();
            project.GuideNodes.Add(new GuideNode { Name = "第二节点", TrackId = track.Id,
                OriginalCoordinate = new GeoCoordinate(24.0025, 113.0025) });
            project.Waypoints.Add(new Waypoint { Name = "第二普通点", AssociatedTrackId = track.Id,
                Latitude = 24.0025, Longitude = 113.0025 });
            project.Waypoints.Add(new Waypoint { Name = "第一普通点", AssociatedTrackId = track.Id,
                Latitude = 24.0005, Longitude = 113.0005 });
            var section = track.GetSplitSections().Single(item => item.Id == second.Id);
            var path = Path.Combine(root, "section.kmz");
            await new ProjectMergedExportService().ExportTrackAsync(project, track, section,
                TrackFileFormat.Kmz, path, false);
            var imported = await new KmzTrackImporter().ImportAsync(path);
            Assert.Equal("第二分段", Assert.Single(imported.Tracks).Name);
            Assert.Single(imported.Tracks[0].Segments);
            Assert.Equal(2, imported.Tracks[0].GetPointCount());
            Assert.Equal(new HashSet<string> { "第二节点", "第二普通点" },
                imported.Waypoints.Select(item => item.Name!).ToHashSet());
            Assert.Empty(imported.MediaAttachments);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("<div><a href=\"\" target=\"_blank\"><img src=\"https://example.com/a.jpg\"/>非合法营地</a></div>")]
    [InlineData("&lt;div&gt;&lt;a href=&quot;&quot;&gt;&lt;img src=&quot;https://example.com/a.jpg&quot;/&gt;非合法营地&lt;/a&gt;&lt;/div&gt;")]
    [InlineData("&amp;lt;div&amp;gt;&amp;lt;a href=&amp;quot;&amp;quot;&amp;gt;&amp;lt;img src=&amp;quot;https://example.com/a.jpg&amp;quot;/&amp;gt;非合法营地&amp;lt;/a&amp;gt;&amp;lt;/div&amp;gt;")]
    public async Task LegacyImageWrapper_ExportsOnlyReadableDescription(string description)
    {
        var root = Path.Combine(Path.GetTempPath(), "HikingGuide-LegacyText-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = CreateProject();
            var marker = project.GuideNodes[0];
            marker.Description = description;
            var selection = new MergedExportSelection();
            selection.MarkerIds.Add(marker.Id);
            var path = Path.Combine(root, "clean.kml");
            await new ProjectMergedExportService().ExportAsync(project, selection,
                TrackFileFormat.Kml, path, false);
            var xml = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("&lt;div", xml);
            Assert.DoesNotContain("&amp;lt;", xml);
            Assert.DoesNotContain("href=", xml);
            var imported = await new KmlTrackImporter().ImportAsync(path);
            Assert.Equal("非合法营地", Assert.Single(imported.Waypoints).Description);
        }
        finally { Directory.Delete(root, true); }
    }

    private static HikingProject CreateProject()
    {
        var project = new HikingProject { Name = "测试合并" };
        var first = new Track { Name = "重复名称" };
        var segment1 = new TrackSegment();
        segment1.Points.Add(new TrackPoint { Latitude = 24, Longitude = 113 });
        segment1.Points.Add(new TrackPoint { Latitude = 24.001, Longitude = 113.001 });
        first.Segments.Add(segment1);
        first.Segments.Add(new TrackSegment());
        first.RecalculateDistances();
        var second = new Track { Name = "重复名称" };
        var segment2 = new TrackSegment();
        segment2.Points.Add(new TrackPoint { Latitude = 25, Longitude = 114 });
        second.Segments.Add(segment2);
        second.RecalculateDistances();
        project.AddTrack(first);
        project.AddTrack(second);
        var waypoint = new Waypoint { Name = "普通点", Latitude = 24, Longitude = 113 };
        var guide = new GuideNode
        {
            Name = "山顶", OriginalCoordinate = new GeoCoordinate(24.0005, 113.0005),
            SnappedCoordinate = new GeoCoordinate(24.0005, 113.0005), TrackId = first.Id,
            Type = WaypointType.Custom, CustomTypeName = "观景峰"
        };
        project.Waypoints.Add(waypoint);
        project.GuideNodes.Add(guide);
        var blob = new MediaBlob { Sha256 = Convert.ToHexString(SHA256.HashData(Png)), MimeType = "image/png",
            FileExtension = ".png", ByteLength = Png.Length, Width = 1, Height = 1, PendingBytes = Png };
        blob.StoredPath = $"media/originals/{blob.Id:D}.png";
        project.MediaBlobs.Add(blob);
        project.MediaAttachments.Add(new MediaAttachment { OwnerType = MediaOwnerType.Waypoint,
            OwnerId = waypoint.Id, MediaBlobId = blob.Id });
        project.MediaAttachments.Add(new MediaAttachment { OwnerType = MediaOwnerType.GuideNode,
            OwnerId = guide.Id, MediaBlobId = blob.Id });
        project.ExternalMediaReferences.Add(new ExternalMediaReference { OwnerType = MediaOwnerType.GuideNode,
            OwnerId = guide.Id, Url = "https://example.com/image?id=1", Caption = "远景" });
        project.MarkSaved();
        return project;
    }
}
