using System.Diagnostics;
using System.Text.Json;
using HikingGuide.Application.Maps;
using HikingGuide.Core.Models;

namespace HikingGuide.Application.Tests;

public sealed class MapDisplayTests
{
    [Fact]
    public void Protocol_AcceptsTrackEditEscapeCancellationWithBoundContext()
    {
        var project = Guid.NewGuid(); var track = Guid.NewGuid(); var session = Guid.NewGuid();
        var value = MapProtocol.TryParseEvent(JsonSerializer.Serialize(new { protocolVersion = 1, type = "TrackEditPreviewCancelled", projectId = project, trackId = track, editSessionId = session, geometryRevision = 9 }));
        Assert.NotNull(value); Assert.Equal(project, value.ProjectId); Assert.Equal(track, value.TrackId);
        Assert.Equal(session, value.EditSessionId); Assert.Equal(9, value.GeometryRevision);
    }

    [Fact]
    public void DisplayAndGeoJson_IncludeTargetGeometryRevisionWithoutChangingOriginal()
    {
        var track = CreateTrack(25, 1);
        track.NotifyGeometryChanged();
        var project = new HikingProject();
        project.AddTrack(track);
        var data = new MapDisplayDataService(new DouglasPeuckerTrackDisplaySimplifier()).Create(project, 15, 1);
        Assert.Equal(track.GeometryRevision, data.Tracks.Single().GeometryRevision);
        using var json = JsonDocument.Parse(new MapGeoJsonBuilder().BuildTracks(data));
        Assert.Equal(track.GeometryRevision, json.RootElement.GetProperty("features")[0].GetProperty("properties").GetProperty("geometryRevision").GetInt64());
        Assert.Equal(25, track.GetPointCount());
    }

    [Fact]
    public void Simplify_PreservesEndpointsSegmentsDirectionAndOriginalTrack()
    {
        var track = CreateTrack(250, 2);
        var original = track.Segments.SelectMany(segment => segment.Points)
            .Select(point => (point.Id, point.Longitude, point.Latitude)).ToArray();

        var feature = new DouglasPeuckerTrackDisplaySimplifier().Simplify(track, true, 8);

        Assert.Equal(2, feature.Segments.Count);
        for (var index = 0; index < 2; index++)
        {
            Assert.Equal(track.Segments[index].Points[0].Longitude, feature.Segments[index][0].Longitude);
            Assert.Equal(track.Segments[index].Points[^1].Longitude, feature.Segments[index][^1].Longitude);
        }
        Assert.True(feature.DisplayPointCount < feature.OriginalPointCount);
        Assert.Equal(original, track.Segments.SelectMany(segment => segment.Points)
            .Select(point => (point.Id, point.Longitude, point.Latitude)).ToArray());
    }

    [Fact]
    public void Simplify_HigherZoomRetainsAtLeastAsManyPoints()
    {
        var track = CreateTrack(2000, 1);
        var simplifier = new DouglasPeuckerTrackDisplaySimplifier();
        var low = simplifier.Simplify(track, false, 6).DisplayPointCount;
        var middle = simplifier.Simplify(track, false, 14).DisplayPointCount;
        var high = simplifier.Simplify(track, false, 20).DisplayPointCount;
        Assert.True(low <= middle);
        Assert.True(middle <= high);
    }

    [Fact]
    public void Simplify_GeometryRevisionInvalidatesCachedDisplay()
    {
        var track = CreateTrack(100, 1);
        var simplifier = new DouglasPeuckerTrackDisplaySimplifier();
        var before = simplifier.Simplify(track, false, 20);
        track.Segments[0].Points.Insert(50, new TrackPoint { Longitude = 110.2, Latitude = 35.2 });
        track.NotifyGeometryChanged();

        var after = simplifier.Simplify(track, false, 20);

        Assert.NotEqual(before.OriginalPointCount, after.OriginalPointCount);
    }

    [Fact]
    public void Simplify_OneHundredThousandPoints_CompletesWithoutMutatingOriginal()
    {
        var track = CreateTrack(100_000, 1);
        var firstId = track.Segments[0].Points[0].Id;
        var lastId = track.Segments[0].Points[^1].Id;
        var timer = Stopwatch.StartNew();

        var feature = new DouglasPeuckerTrackDisplaySimplifier().Simplify(track, true, 12);

        timer.Stop();
        Assert.Equal(100_000, track.GetPointCount());
        Assert.Equal(firstId, track.Segments[0].Points[0].Id);
        Assert.Equal(lastId, track.Segments[0].Points[^1].Id);
        Assert.InRange(feature.DisplayPointCount, 2, 99_999);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), $"简化耗时 {timer.Elapsed}");
    }

    [Fact]
    public void GeoJson_UsesLongitudeLatitudeAndStableIdsWithChineseText()
    {
        var trackId = Guid.NewGuid();
        var waypointId = Guid.NewGuid();
        var data = new MapDisplayData(Guid.NewGuid(), 7, 15,
        [
            new MapTrackFeature(trackId, "中文轨迹", true,
                [[new MapCoordinate(113.25, 23.5), new MapCoordinate(113.3, 23.6)]], 2, 2)
        ],
        [new MapWaypointFeature(waypointId, "补水点", WaypointType.Water, new MapCoordinate(114.1, 24.2))]);

        var payload = new MapGeoJsonBuilder().Build(data);
        using var tracks = JsonDocument.Parse(payload.TracksGeoJson);
        using var waypoints = JsonDocument.Parse(payload.WaypointsGeoJson);
        var trackFeature = tracks.RootElement.GetProperty("features")[0];
        var coordinate = trackFeature.GetProperty("geometry").GetProperty("coordinates")[0][0];

        Assert.Equal(trackId.ToString(), trackFeature.GetProperty("id").GetString());
        Assert.Equal("中文轨迹", trackFeature.GetProperty("properties").GetProperty("name").GetString());
        Assert.Equal(113.25, coordinate[0].GetDouble());
        Assert.Equal(23.5, coordinate[1].GetDouble());
        Assert.Equal(waypointId.ToString(), waypoints.RootElement.GetProperty("features")[0].GetProperty("id").GetString());
        Assert.Equal("补水点", waypoints.RootElement.GetProperty("features")[0].GetProperty("properties").GetProperty("name").GetString());
    }

    [Fact]
    public void MarkerGeoJson_KeepsDisplayName_ButOnlyNamedMarkersGetZ15LabelText()
    {
        var data = new MapDisplayData(Guid.NewGuid(), 1, 15, [],
        [
            new MapWaypointFeature(Guid.NewGuid(), "  水源  ", WaypointType.Water, new MapCoordinate(113, 23)),
            new MapWaypointFeature(Guid.NewGuid(), "  ", WaypointType.Water, new MapCoordinate(114, 24))
        ]);
        var payload = new MapGeoJsonBuilder().Build(data);
        using var waypoints = JsonDocument.Parse(payload.WaypointsGeoJson);
        var features = waypoints.RootElement.GetProperty("features");
        Assert.Equal("水源", features[0].GetProperty("properties").GetProperty("namedLabel").GetString());
        Assert.Equal("", features[1].GetProperty("properties").GetProperty("namedLabel").GetString());
        var project = new HikingProject();
        project.GuideNodes.Add(new GuideNode { Name = "  山顶  " });
        project.GuideNodes.Add(new GuideNode { Name = " " });
        var nodes = new GuideMapGeoJsonBuilder().Build(project).Nodes.GetProperty("features");
        Assert.Equal("山顶", nodes[0].GetProperty("properties").GetProperty("namedLabel").GetString());
        Assert.Equal("", nodes[1].GetProperty("properties").GetProperty("namedLabel").GetString());
    }

    [Fact]
    public void GeoJson_PreservesMultipleTracksAndSeparateSegments()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var data = new MapDisplayData(Guid.NewGuid(), 1, 12,
        [
            new MapTrackFeature(first, "轨迹甲", true,
            [
                [new MapCoordinate(110, 20), new MapCoordinate(111, 21)],
                [new MapCoordinate(120, 30), new MapCoordinate(121, 31)]
            ], 4, 4),
            new MapTrackFeature(second, "轨迹乙", false,
                [[new MapCoordinate(100, 10), new MapCoordinate(101, 11)]], 2, 2)
        ], []);

        using var json = JsonDocument.Parse(new MapGeoJsonBuilder().BuildTracks(data));

        Assert.Equal(2, json.RootElement.GetProperty("features").GetArrayLength());
        Assert.Equal(2, json.RootElement.GetProperty("features")[0].GetProperty("geometry").GetProperty("coordinates").GetArrayLength());
        Assert.Equal("MultiLineString", json.RootElement.GetProperty("features")[0].GetProperty("geometry").GetProperty("type").GetString());
    }

    [Fact]
    public void GeoJson_OneThousandWaypointsRemainIndividualFeatures()
    {
        var waypoints = Enumerable.Range(0, 1000)
            .Select(index => new MapWaypointFeature(Guid.NewGuid(), $"标注{index}", WaypointType.Normal,
                new MapCoordinate(110 + index * 0.0001, 20 + index * 0.0001))).ToArray();
        var data = new MapDisplayData(Guid.NewGuid(), 1, 12, [], waypoints);

        using var json = JsonDocument.Parse(new MapGeoJsonBuilder().BuildWaypoints(data));

        Assert.Equal(1000, json.RootElement.GetProperty("features").GetArrayLength());
    }

    [Fact]
    public void Simplify_HonorsPreCanceledRequest()
    {
        var cancellation = new CancellationToken(canceled: true);
        Assert.Throws<OperationCanceledException>(() =>
            new DouglasPeuckerTrackDisplaySimplifier().Simplify(CreateTrack(1000, 2), false, 12, cancellation));
    }

    [Fact]
    public void RevisionGate_RejectsProjectAAfterProjectBSupersedesIt()
    {
        var gate = new MapRevisionGate();
        var projectA = Guid.NewGuid();
        var projectB = Guid.NewGuid();
        var revisionA = gate.Begin(projectA);
        var revisionB = gate.Begin(projectB);

        Assert.False(gate.IsCurrent(projectA, revisionA));
        Assert.True(gate.IsCurrent(projectB, revisionB));
    }

    [Fact]
    public void DisplayData_EmphasizesMainGuideTrackNotSelectedTrack()
    {
        var first = CreateTrack(10, 1);
        var second = CreateTrack(10, 1);
        var project = new HikingProject();
        project.AddTrack(first);
        project.AddTrack(second, false);
        project.SetActiveTrack(second.Id);

        var data = new MapDisplayDataService(new DouglasPeuckerTrackDisplaySimplifier()).Create(project, 12, 1);

        Assert.True(Assert.Single(data.Tracks, track => track.TrackId == first.Id).IsActive);
        Assert.False(Assert.Single(data.Tracks, track => track.TrackId == second.Id).IsActive);
    }

    [Fact]
    public void Protocol_SerializesVersionProjectAndRevision()
    {
        var projectId = Guid.NewGuid();
        using var json = JsonDocument.Parse(MapProtocol.SerializeCommand("SetTracks", new { count = 2 }, projectId, 9));
        Assert.Equal(MapProtocol.Version, json.RootElement.GetProperty("protocolVersion").GetInt32());
        Assert.Equal("SetTracks", json.RootElement.GetProperty("type").GetString());
        Assert.Equal(projectId.ToString(), json.RootElement.GetProperty("projectId").GetString());
        Assert.Equal(9, json.RootElement.GetProperty("revision").GetInt64());
    }

    [Theory]
    [InlineData("MapReady")]
    [InlineData("ViewChanged")]
    [InlineData("TrackClicked")]
    [InlineData("WaypointClicked")]
    [InlineData("MapRightClicked")]
    [InlineData("MarkerRightClicked")]
    [InlineData("MouseMoved")]
    [InlineData("MouseLeft")]
    [InlineData("MapError")]
    [InlineData("TileError")]
    [InlineData("RenderMetrics")]
    public void Protocol_ParsesEveryKnownEvent(string type)
    {
        var value = MapProtocol.TryParseEvent($"{{\"protocolVersion\":1,\"type\":\"{type}\",\"revision\":5}}");
        Assert.NotNull(value);
        Assert.Equal(type, value.Type);
        Assert.Equal(5, value.Revision);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("{\"protocolVersion\":2,\"type\":\"MapReady\"}")]
    [InlineData("{\"protocolVersion\":1,\"type\":\"Unknown\"}")]
    public void Protocol_RejectsMalformedUnknownOrWrongVersion(string json) =>
        Assert.Null(MapProtocol.TryParseEvent(json));

    private static Track CreateTrack(int pointCount, int segmentCount)
    {
        var track = new Track { Name = "性能测试轨迹" };
        for (var segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
        {
            var segment = new TrackSegment();
            for (var index = 0; index < pointCount; index++)
            {
                var offset = segmentIndex * 0.1;
                segment.Points.Add(new TrackPoint
                {
                    Longitude = 113 + offset + index * 0.00001,
                    Latitude = 23 + offset + Math.Sin(index / 30d) * 0.00005
                });
            }
            track.Segments.Add(segment);
        }
        return track;
    }
}
