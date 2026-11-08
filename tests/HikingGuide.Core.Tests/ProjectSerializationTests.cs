using System.Text.Json;
using System.Text.Json.Serialization;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;

namespace HikingGuide.Core.Tests;

public sealed class ProjectSerializationTests
{
    [Fact]
    public void Project_JsonRoundTrip_PreservesCoreData()
    {
        var trackPoint = new TrackPoint
        {
            Latitude = 30.123,
            Longitude = 110.456,
            SourceElevation = 1234,
            DemElevation = 1240,
            OriginalAttributes = new Dictionary<string, string?> { ["来源"] = "测试" }
        };
        var waypoint = new Waypoint
        {
            Name = "水源",
            Latitude = 30.2,
            Longitude = 110.5,
            Type = WaypointType.Water
        };
        var guideNode = new GuideNode
        {
            Name = "山峰",
            Type = WaypointType.Summit,
            OriginalCoordinate = new GeoCoordinate(30.3, 110.6),
            SnappedCoordinate = new GeoCoordinate(30.3001, 110.6001),
            ManualData = new ManualGuideData { Notes = "人工备注" }
        };
        var track = new Track
        {
            Name = "测试轨迹",
            Segments = [new TrackSegment { Points = [trackPoint] }]
        };
        var project = new HikingProject
        {
            Name = "序列化测试项目",
            Description = "测试说明",
            ManualData = new ManualGuideData { Plan = "第一天计划" },
            AnalysisProfile = AnalysisProfile.CreateTestProfile()
        };
        project.AddTrack(track);
        project.Waypoints.Add(waypoint);
        project.GuideNodes.Add(guideNode);
        project.MarkSaved();

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var json = JsonSerializer.Serialize(project, options);
        var restored = JsonSerializer.Deserialize<HikingProject>(json, options);

        Assert.NotNull(restored);
        Assert.Equal(project.Id, restored.Id);
        Assert.Equal(project.Name, restored.Name);
        Assert.Equal(project.Tracks[0].Id, restored.Tracks[0].Id);
        Assert.Equal(project.ActiveTrackId, restored.ActiveTrackId);
        Assert.Equal(trackPoint.Id, restored.Tracks[0].Segments[0].Points[0].Id);
        Assert.Equal(waypoint.Id, restored.Waypoints[0].Id);
        Assert.Equal(guideNode.Id, restored.GuideNodes[0].Id);
        Assert.Equal("人工备注", restored.GuideNodes[0].ManualData?.Notes);
        Assert.Equal("第一天计划", restored.ManualData.Plan);
        Assert.Equal("自动测试配置", restored.AnalysisProfile.Name);
        Assert.False(restored.IsDirty);
    }
}
