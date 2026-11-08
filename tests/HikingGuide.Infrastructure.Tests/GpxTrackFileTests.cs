using HikingGuide.Application.TrackFiles;
using HikingGuide.Core.Models;

namespace HikingGuide.Infrastructure.Tests;

public sealed class GpxTrackFileTests
{
    [Fact]
    public async Task Import_SingleTrack_PreservesOrderAndCalculatesDistance()
    {
        using var context = new TrackFileTestContext();

        var result = await context.Service.ImportAsync(
            TrackFileTestContext.DataPath("GPX", "01_单轨迹.gpx"));

        Assert.True(result.IsSuccess);
        var track = Assert.Single(result.Tracks);
        Assert.Equal("测试轨迹", track.Name);
        Assert.Equal(2, track.GetPointCount());
        Assert.Equal(23.10000001, track.StartPoint?.Latitude);
        Assert.Equal(23.10100002, track.EndPoint?.Latitude);
        Assert.True(track.EndPoint?.DistanceFromStart > 0);
    }

    [Fact]
    public async Task Import_MultipleTracks_ReturnsEveryTrack()
    {
        using var context = new TrackFileTestContext();

        var result = await context.Service.ImportAsync(
            TrackFileTestContext.DataPath("GPX", "09_多轨迹.gpx"));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Tracks.Count);
        Assert.Equal(new[] { "轨迹A", "轨迹B" }, result.Tracks.Select(track => track.Name));
    }

    [Fact]
    public async Task Import_MultipleSegments_DoesNotAddConnectionDistance()
    {
        using var context = new TrackFileTestContext();

        var result = await context.Service.ImportAsync(
            TrackFileTestContext.DataPath("GPX", "02_多轨迹段.gpx"));

        var track = Assert.Single(result.Tracks);
        Assert.Equal(2, track.Segments.Count);
        var expected = track.Segments.Sum(segment => segment.GetLength());
        Assert.Equal(expected, track.GetTotalLength(), 6);
        var secondSegmentStart = Assert.IsType<TrackPoint>(track.Segments[1].StartPoint);
        Assert.Equal(track.Segments[0].GetLength(), secondSegmentStart.DistanceFromStart, 6);
    }

    [Fact]
    public async Task Import_Waypoint_ReadsFieldsAndPreservesExtension()
    {
        using var context = new TrackFileTestContext();

        var result = await context.Service.ImportAsync(
            TrackFileTestContext.DataPath("GPX", "03_带标注点.gpx"));

        var waypoint = Assert.Single(result.Waypoints);
        Assert.Equal("水源", waypoint.Name);
        Assert.Equal("可靠水源", waypoint.Description);
        Assert.Equal(1278.43, waypoint.SourceElevation);
        Assert.Null(waypoint.DemElevation);
        Assert.Equal(WaypointType.Water, waypoint.Type);
        Assert.Equal(TimeSpan.FromHours(8), waypoint.Timestamp?.Offset);
        Assert.True(waypoint.OriginalAttributes.ContainsKey("gpx:extensions"));
        Assert.Contains(result.Warnings, warning => warning.Code == "PreservedGpxExtension");
    }

    [Fact]
    public async Task Import_Timestamps_PreserveZuluAndOffsetSemantics()
    {
        using var context = new TrackFileTestContext();

        var result = await context.Service.ImportAsync(
            TrackFileTestContext.DataPath("GPX", "04_带时间.gpx"));

        var points = result.Tracks[0].Segments[0].Points;
        Assert.Equal(TimeSpan.Zero, points[0].Timestamp?.Offset);
        Assert.Equal(TimeSpan.FromHours(8), points[1].Timestamp?.Offset);
    }

    [Fact]
    public async Task Import_ElevationsRemainSourceElevationWithDecimals()
    {
        using var context = new TrackFileTestContext();

        var result = await context.Service.ImportAsync(
            TrackFileTestContext.DataPath("GPX", "05_带海拔.gpx"));

        var points = result.Tracks[0].Segments[0].Points;
        Assert.Equal(1278.43, points[0].SourceElevation);
        Assert.Equal(1280.125, points[1].SourceElevation);
        Assert.All(points, point => Assert.Null(point.DemElevation));
    }

    [Fact]
    public async Task Import_ChineseContent_IsNotGarbled()
    {
        using var context = new TrackFileTestContext();

        var result = await context.Service.ImportAsync(
            TrackFileTestContext.DataPath("GPX", "06_中文名称.gpx"));

        Assert.Equal("环船百大纵走", result.Tracks[0].Name);
        Assert.Contains("船底顶", result.Tracks[0].Description);
        Assert.Equal("下撤点", result.Waypoints[0].Name);
    }

    [Fact]
    public async Task Import_OnlyWaypoints_SucceedsWithWarning()
    {
        using var context = new TrackFileTestContext();

        var result = await context.Service.ImportAsync(
            TrackFileTestContext.DataPath("GPX", "07_只有标注点.gpx"));

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Tracks);
        Assert.Equal(2, result.Waypoints.Count);
        Assert.Contains(result.Warnings, warning => warning.Code == "NoTracks");
    }

    [Fact]
    public async Task Import_DamagedXml_ReturnsChineseError()
    {
        using var context = new TrackFileTestContext();

        var result = await context.Service.ImportAsync(
            TrackFileTestContext.DataPath("GPX", "08_损坏文件.gpx"));

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.Message.Contains("XML格式错误", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RoundTrip_PreservesSemanticData()
    {
        using var context = new TrackFileTestContext();
        var original = await context.Service.ImportAsync(
            TrackFileTestContext.DataPath("GPX", "03_带标注点.gpx"));
        var output = context.OutputPath("船底顶测试.gpx");

        await context.Service.ExportAsync(
            TrackFileFormat.Gpx,
            new TrackExportRequest
            {
                DocumentName = "船底顶测试",
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
        Assert.Equal(original.Waypoints[0].Name, restored.Waypoints[0].Name);
        Assert.Equal(original.Waypoints[0].Description, restored.Waypoints[0].Description);
        Assert.Equal(original.Waypoints[0].Coordinate, restored.Waypoints[0].Coordinate);
        Assert.Equal(original.Waypoints[0].SourceElevation, restored.Waypoints[0].SourceElevation);
        Assert.Equal(original.Waypoints[0].Timestamp, restored.Waypoints[0].Timestamp);
        Assert.True(restored.Waypoints[0].OriginalAttributes.ContainsKey("gpx:extensions"));
    }
}
