using HikingGuide.Application.TrackFiles;

namespace HikingGuide.Infrastructure.Tests;

public sealed class KmlTrackFileTests
{
    [Fact]
    public async Task P52_MarkersInUniqueTrackFoldersKeepExactOwnership()
    {
        using var context = new TrackFileTestContext();
        var path = context.OutputPath("folder-ownership.kml");
        await File.WriteAllTextAsync(path, """
            <kml><Document>
              <Folder><name>甲组</name>
                <Placemark><name>同名线</name><LineString><coordinates>113,23 113.01,23</coordinates></LineString></Placemark>
                <Placemark><name>水源甲</name><Point><coordinates>113.005,23</coordinates></Point></Placemark>
              </Folder>
              <Folder><name>乙组</name>
                <Placemark><name>同名线</name><LineString><coordinates>114,24 114.01,24</coordinates></LineString></Placemark>
                <Placemark><name>水源乙</name><Point><coordinates>114.005,24</coordinates></Point></Placemark>
              </Folder>
            </Document></kml>
            """);
        var parsed = await context.Service.ImportAsync(path);
        Assert.True(parsed.IsSuccess);
        Assert.Equal(2, parsed.Tracks.Count);
        Assert.Equal("同名线", parsed.Tracks[0].Name);
        Assert.Equal("同名线", parsed.Tracks[1].Name);
        Assert.NotEqual(parsed.Tracks[0].Id, parsed.Tracks[1].Id);
        Assert.Equal(parsed.Tracks[0].Id, parsed.Waypoints[0].AssociatedTrackId);
        Assert.Equal(parsed.Tracks[1].Id, parsed.Waypoints[1].AssociatedTrackId);
    }

    [Fact]
    public async Task P52_AmbiguousFolderDoesNotGuessMarkerTrack()
    {
        using var context = new TrackFileTestContext();
        var path = context.OutputPath("ambiguous-folder.kml");
        await File.WriteAllTextAsync(path, """
            <kml><Document><Folder><name>两条线</name>
              <Placemark><name>A</name><LineString><coordinates>113,23 113.01,23</coordinates></LineString></Placemark>
              <Placemark><name>B</name><LineString><coordinates>114,24 114.01,24</coordinates></LineString></Placemark>
              <Placemark><name>不确定标注</name><Point><coordinates>113.005,23</coordinates></Point></Placemark>
            </Folder></Document></kml>
            """);
        var parsed = await context.Service.ImportAsync(path);
        Assert.True(parsed.IsSuccess);
        Assert.Null(Assert.Single(parsed.Waypoints).AssociatedTrackId);
    }

    [Fact]
    public async Task Import_LineString_UsesLongitudeLatitudeOrder()
    {
        using var context = new TrackFileTestContext();

        var result = await context.Service.ImportAsync(
            TrackFileTestContext.DataPath("KML", "01_LineString.kml"));

        Assert.True(result.IsSuccess);
        var track = Assert.Single(result.Tracks);
        Assert.Equal(23.10000001, track.StartPoint?.Latitude);
        Assert.Equal(113.20000001, track.StartPoint?.Longitude);
        Assert.Equal(100.25, track.StartPoint?.SourceElevation);
        Assert.True(track.EndPoint?.DistanceFromStart > 0);
    }

    [Fact]
    public async Task Import_Point_ReadsDescriptionElevationAndTimestamp()
    {
        using var context = new TrackFileTestContext();

        var result = await context.Service.ImportAsync(
            TrackFileTestContext.DataPath("KML", "02_Point.kml"));

        var waypoint = Assert.Single(result.Waypoints);
        Assert.Equal("水源", waypoint.Name);
        Assert.Equal("可靠<br/>水源", waypoint.Description);
        Assert.Equal(23.12345678, waypoint.Latitude);
        Assert.Equal(113.23456789, waypoint.Longitude);
        Assert.Equal(1278.43, waypoint.SourceElevation);
        Assert.Null(waypoint.DemElevation);
        Assert.Equal(TimeSpan.FromHours(8), waypoint.Timestamp?.Offset);
        Assert.Equal("water-point", waypoint.OriginalAttributes["kml:Placemark:attribute:id"]);
    }

    [Fact]
    public async Task Import_LineStringAndPoint_ReturnsBothKinds()
    {
        using var context = new TrackFileTestContext();

        var result = await context.Service.ImportAsync(
            TrackFileTestContext.DataPath("KML", "03_LineString加Point.kml"));

        Assert.Single(result.Tracks);
        Assert.Single(result.Waypoints);
        Assert.Equal("营地", result.Waypoints[0].Name);
    }

    [Fact]
    public async Task Import_MultiGeometry_MapsLineStringsToSegmentsAndPointToWaypoint()
    {
        using var context = new TrackFileTestContext();

        var result = await context.Service.ImportAsync(
            TrackFileTestContext.DataPath("KML", "04_MultiGeometry.kml"));

        var track = Assert.Single(result.Tracks);
        Assert.Equal(2, track.Segments.Count);
        Assert.Equal(4, track.GetPointCount());
        Assert.Single(result.Waypoints);
        Assert.Equal("复合对象", result.Waypoints[0].Name);
    }

    [Fact]
    public async Task Import_NestedFolders_IsRecursiveAndPreservesFolderPath()
    {
        using var context = new TrackFileTestContext();

        var result = await context.Service.ImportAsync(
            TrackFileTestContext.DataPath("KML", "05_Folder嵌套.kml"));

        var waypoint = Assert.Single(result.Waypoints);
        Assert.Equal("深层标注", waypoint.Name);
        Assert.Equal("第一层/第二层", waypoint.OriginalAttributes["kml:folderPath"]);
    }

    [Fact]
    public async Task Import_ChineseContentAndHighPrecision_ArePreserved()
    {
        using var context = new TrackFileTestContext();

        var result = await context.Service.ImportAsync(
            TrackFileTestContext.DataPath("KML", "06_中文名称.kml"));

        Assert.Equal("环船百大纵走规划攻略", result.Metadata["kml:name"]);
        Assert.Equal("后山村", result.Tracks[0].Name);
        Assert.Contains("船底顶", Assert.IsType<string>(result.Tracks[0].Description));
        Assert.Equal(113.26384023, result.Tracks[0].StartPoint?.Longitude);
        Assert.Equal(1278.43, result.Tracks[0].StartPoint?.SourceElevation);
        Assert.Equal("联山农庄民宿", result.Waypoints[0].Name);
    }

    [Fact]
    public async Task Import_WithoutAltitude_LeavesSourceElevationNull()
    {
        using var context = new TrackFileTestContext();

        var result = await context.Service.ImportAsync(
            TrackFileTestContext.DataPath("KML", "07_无海拔.kml"));

        Assert.All(
            result.Tracks[0].Segments[0].Points,
            point => Assert.Null(point.SourceElevation));
    }

    [Fact]
    public async Task Import_DamagedXml_ReturnsChineseError()
    {
        using var context = new TrackFileTestContext();

        var result = await context.Service.ImportAsync(
            TrackFileTestContext.DataPath("KML", "08_损坏文件.kml"));

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.Message.Contains("XML格式错误", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RoundTrip_PreservesSemanticDataAndDirection()
    {
        using var context = new TrackFileTestContext();
        var original = await context.Service.ImportAsync(
            TrackFileTestContext.DataPath("KML", "06_中文名称.kml"));
        var output = context.OutputPath("环船百大纵走（往返测试）.kml");

        await context.Service.ExportAsync(
            TrackFileFormat.Kml,
            new TrackExportRequest
            {
                DocumentName = original.Metadata["kml:name"],
                Tracks = original.Tracks,
                Waypoints = original.Waypoints,
                Metadata = original.Metadata
            },
            output);
        var restored = await context.Service.ImportAsync(output);

        Assert.True(restored.IsSuccess);
        Assert.Equal(original.Tracks.Count, restored.Tracks.Count);
        Assert.Equal(original.TrackSegmentCount, restored.TrackSegmentCount);
        Assert.Equal(original.TrackPointCount, restored.TrackPointCount);
        Assert.Equal(original.Waypoints.Count, restored.Waypoints.Count);
        Assert.Equal(original.Tracks[0].StartPoint?.Coordinate, restored.Tracks[0].StartPoint?.Coordinate);
        Assert.Equal(original.Tracks[0].EndPoint?.Coordinate, restored.Tracks[0].EndPoint?.Coordinate);
        Assert.Equal(original.Tracks[0].StartPoint?.SourceElevation, restored.Tracks[0].StartPoint?.SourceElevation);
        Assert.Equal(original.Tracks[0].Name, restored.Tracks[0].Name);
        Assert.Equal(original.Tracks[0].Description, restored.Tracks[0].Description);
        Assert.Equal(original.Waypoints[0].Name, restored.Waypoints[0].Name);
        Assert.Equal(original.Waypoints[0].Description, restored.Waypoints[0].Description);
    }

    [Fact]
    public async Task PointRoundTrip_PreservesTimestampStyleAndXmlId()
    {
        using var context = new TrackFileTestContext();
        var original = await context.Service.ImportAsync(
            TrackFileTestContext.DataPath("KML", "02_Point.kml"));
        var output = context.OutputPath("Point往返.kml");

        await context.Service.ExportAsync(
            TrackFileFormat.Kml,
            new TrackExportRequest
            {
                Waypoints = original.Waypoints,
                Metadata = original.Metadata
            },
            output);
        var restored = await context.Service.ImportAsync(output);

        var waypoint = Assert.Single(restored.Waypoints);
        Assert.Equal(original.Waypoints[0].Timestamp, waypoint.Timestamp);
        Assert.Equal("#water", waypoint.OriginalAttributes["kml:styleUrl"]);
        Assert.Equal("water-point", waypoint.OriginalAttributes["kml:Placemark:attribute:id"]);
    }
}
