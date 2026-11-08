using HikingGuide.Application.Guides;
using HikingGuide.Application.Maps;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Projects;
using System.Text.Json;

namespace HikingGuide.Infrastructure.Tests;

public sealed class ProjectImportBatchTests
{
    [Fact]
    public async Task KmzInternalTracks_BecomeOneNamedParent_WithSplitSectionsAndOwnMarkers()
    {
        using var context = new TrackFileTestContext();
        var path = context.OutputPath("山脊合并线路.kmz");
        using (var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("doc.kml").Open());
            await writer.WriteAsync("""
                <kml><Document><name>不同文档名</name>
                 <Placemark><name>甲段</name><LineString><coordinates>113,23,10 113.01,23,20</coordinates></LineString></Placemark>
                 <Placemark><name>乙段</name><MultiGeometry><LineString><coordinates>114,24,30 114.01,24,40</coordinates></LineString><LineString><coordinates>115,25,50 115.01,25,60</coordinates></LineString></MultiGeometry></Placemark>
                 <Placemark><name>标注</name><description><![CDATA[<img src="https://example.com/image.jpg"/>]]></description><Point><coordinates>113,23</coordinates></Point></Placemark>
                </Document></kml>
                """);
        }
        var raw = await context.Service.ImportAsync(path);
        var result = await new ProjectImportBatch(context.Service).ReadAsync([path], new HikingProject());
        Assert.True(result.IsSuccess); var track = Assert.Single(result.Tracks);
        Assert.Equal("山脊合并线路", track.Name);
        Assert.Equal(raw.Tracks.Sum(t => t.GetPointCount()), track.GetPointCount());
        Assert.Equal(raw.Tracks.Sum(t => t.GetTotalLength()), track.GetTotalLength(), 6);
        Assert.Equal(raw.Tracks.Sum(t => t.Segments.Count), track.Segments.Count);
        Assert.Equal(raw.Tracks.Count, track.GetSplitSections().Count);
        Assert.Equal(raw.Tracks.Select(t => t.Name), track.GetSplitSections().Select(s => s.Name));
        Assert.Equal(track.Id, Assert.Single(result.Waypoints).AssociatedTrackId);
        Assert.Equal(raw.ExternalMediaReferences.Count, result.ExternalMediaReferences.Count);
        var p = new HikingProject(); var session = ProjectImportSession.Create(result, path, p);
        new ProjectImportService().Commit(session, p, true);
        var saved = context.OutputPath("分段.hikeproj"); var storage = new ProjectFileService();
        await storage.SaveAsync(p, saved, false); var reopened = await storage.LoadAsync(saved);
        Assert.Equal(track.GetSplitSections().Select(s => s.Name), Assert.Single(reopened.Tracks).GetSplitSections().Select(s => s.Name));
        Assert.Equal(track.Id, Assert.Single(reopened.Waypoints).AssociatedTrackId);
    }

    [Fact]
    public async Task MultipleFiles_PreserveFileNamesAndOwnMarkers_AndPersistDifferentColors()
    {
        using var context = new TrackFileTestContext();
        var gpx = context.OutputPath("第一条原文件.gpx");
        var kml = context.OutputPath("第二条原文件.kml");
        await File.WriteAllTextAsync(gpx, """
            <gpx version="1.1" creator="test" xmlns="http://www.topografix.com/GPX/1/1">
              <wpt lat="23" lon="113"><name>甲标注</name></wpt>
              <trk><name>轨迹片段1</name><trkseg><trkpt lat="23" lon="113"/><trkpt lat="23.01" lon="113.01"/></trkseg></trk>
            </gpx>
            """);
        await File.WriteAllTextAsync(kml, """
            <kml><Document><name>文档名</name>
              <Folder><name>标注点</name><Placemark><name>乙标注</name><Point><coordinates>114,24</coordinates></Point></Placemark></Folder>
              <Folder><name>轨迹</name><Placemark><name>轨迹片段1</name><LineString><coordinates>114,24 114.01,24.01</coordinates></LineString></Placemark></Folder>
            </Document></kml>
            """);
        var project = new HikingProject { Name = "批量导入验证" };
        var result = await new ProjectImportBatch(context.Service).ReadAsync([gpx, kml], project);
        Assert.True(result.IsSuccess);
        Assert.Equal(["第一条原文件", "第二条原文件"], result.Tracks.Select(track => track.Name));
        Assert.Equal(result.Tracks[0].Id, result.Waypoints[0].AssociatedTrackId);
        Assert.Equal(result.Tracks[1].Id, result.Waypoints[1].AssociatedTrackId);
        Assert.All(result.Tracks, track => Assert.True(TrackDisplayColor.IsValid(track.DisplayColor)));
        Assert.NotEqual(result.Tracks[0].DisplayColor, result.Tracks[1].DisplayColor);
        var session = ProjectImportSession.Create(result, "两个文件", project);
        new ProjectImportService().Commit(session, project, true);
        Assert.Equal(0, project.Waypoints.Count(point => point.AssociatedTrackId is null));

        var projectFile = context.OutputPath("测试.hikeproj");
        var storage = new ProjectFileService();
        await storage.SaveAsync(project, projectFile, false);
        var reopened = await storage.LoadAsync(projectFile);
        Assert.Equal(project.Tracks.Select(track => track.DisplayColor), reopened.Tracks.Select(track => track.DisplayColor));
        Assert.Equal(0, reopened.Waypoints.Count(point => point.AssociatedTrackId is null));
        Assert.Equal(2, reopened.Tracks.Count);
        var display = new MapDisplayDataService(new DouglasPeuckerTrackDisplaySimplifier()).Create(reopened, 14, 1);
        using var geoJson = JsonDocument.Parse(new MapGeoJsonBuilder().BuildTracks(display));
        var renderedColors = geoJson.RootElement.GetProperty("features").EnumerateArray()
            .Select(feature => feature.GetProperty("properties").GetProperty("color").GetString()).ToArray();
        Assert.Equal(reopened.Tracks.Select(track => track.DisplayColor), renderedColors);
    }

    [Fact]
    public void GeneratedColors_AreValidAndNeverRepeatUsedColors()
    {
        var tracks = new List<Track>();
        for (var index = 0; index < 200; index++)
        {
            var color = TrackDisplayColor.NextUnique(tracks);
            Assert.True(TrackDisplayColor.IsValid(color));
            Assert.DoesNotContain(tracks, track => track.DisplayColor == color);
            tracks.Add(new Track { DisplayColor = color });
        }
        Assert.Equal("#AABBCC", TrackDisplayColor.Normalize("#aabbcc"));
    }
}
