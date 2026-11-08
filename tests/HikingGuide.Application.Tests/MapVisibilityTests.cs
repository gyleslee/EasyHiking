using HikingGuide.Application.Guides;
using HikingGuide.Application.Maps;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;

namespace HikingGuide.Application.Tests;

public sealed class MapVisibilityTests
{
    [Fact]
    public void HiddenTrackIsExcludedOnlyFromMapDisplay()
    {
        var project = new HikingProject();
        var first = MakeTrack(0);
        var second = MakeTrack(1);
        project.AddTrack(first);
        project.AddTrack(second);
        var originalCount = project.Tracks.Sum(track => track.GetPointCount());
        MapLayerVisibility.SetTrackVisible(project.Settings.Map, first.Id, false);

        var display = new MapDisplayDataService(new DouglasPeuckerTrackDisplaySimplifier()).Create(project, 12, 1);

        Assert.Equal(second.Id, Assert.Single(display.Tracks).TrackId);
        Assert.Equal(originalCount, project.Tracks.Sum(track => track.GetPointCount()));
        Assert.False(MapLayerVisibility.IsTrackVisible(project.Settings.Map, first.Id));
        Assert.True(MapLayerVisibility.IsTrackVisible(project.Settings.Map, second.Id));
        MapLayerVisibility.SetTrackVisible(project.Settings.Map, first.Id, true);
        Assert.Equal(2, new MapDisplayDataService(new DouglasPeuckerTrackDisplaySimplifier()).Create(project, 12, 2).Tracks.Count);
    }

    [Fact]
    public void HiddenGuideSegmentKeepsModelAndOtherMapLayers()
    {
        var project = new HikingProject();
        var track = MakeTrack(0);
        project.AddTrack(track);
        var guide = new GuideStructureService();
        foreach (var longitude in new[] { 0.001, 0.009 })
        {
            var node = new GuideNode { TrackId = track.Id, OriginalCoordinate = new GeoCoordinate(0, longitude) };
            guide.SnapNode(node, track);
            project.GuideNodes.Add(node);
        }
        var segment = Assert.Single(guide.RebuildSegments(project));
        project.GuideSegments.Add(segment);
        MapLayerVisibility.SetGuideSegmentVisible(project.Settings.Map, segment.Id, false);

        var hidden = new GuideMapGeoJsonBuilder().Build(project);
        Assert.Equal(2, hidden.Nodes.GetProperty("features").GetArrayLength());
        Assert.Equal(0, hidden.Segments.GetProperty("features").GetArrayLength());
        Assert.Single(project.GuideSegments);

        MapLayerVisibility.SetGuideSegmentVisible(project.Settings.Map, segment.Id, true);
        var shown = new GuideMapGeoJsonBuilder().Build(project);
        Assert.Equal(1, shown.Segments.GetProperty("features").GetArrayLength());
    }

    private static Track MakeTrack(double latitude) => new()
    {
        Segments = [new TrackSegment { Points =
            [new TrackPoint { Latitude = latitude, Longitude = 0 },
             new TrackPoint { Latitude = latitude, Longitude = 0.01 }] }]
    };
}
