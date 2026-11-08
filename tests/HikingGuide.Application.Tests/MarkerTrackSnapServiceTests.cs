using HikingGuide.Application.Guides;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using Xunit;

namespace HikingGuide.Application.Tests;

public sealed class MarkerTrackSnapServiceTests
{
    [Fact]
    public void NearestTrack_SearchesAllProjectTracks_NotJustMainOrActive()
    {
        var (project, main, ordinary) = CreateProject();
        project.ActiveTrackId = main.Id;
        var source = new GeoCoordinate(24.0001, 114.005);
        var service = new MarkerTrackSnapService();
        var nearest = service.FindNearest(project, source, false);
        var guideOnly = service.FindNearest(project, source, true);
        Assert.Same(ordinary, nearest.Track);
        Assert.Same(main, guideOnly.Track);
        Assert.InRange(nearest.Position.DistanceToTrack, 0, 20);
        Assert.Equal(0, main.GeometryRevision);
        Assert.Equal(0, ordinary.GeometryRevision);
    }

    [Fact]
    public void GuideSegmentPreview_ChangesOnlyScratchAndPreservesOldManualData()
    {
        var (project, main, _) = CreateProject();
        var guideService = new GuideStructureService();
        var first = new GuideNode { TrackId = main.Id,
            OriginalCoordinate = new GeoCoordinate(23, 113.003) };
        var second = new GuideNode { TrackId = main.Id,
            OriginalCoordinate = new GeoCoordinate(23, 113.008) };
        guideService.SnapNode(first, main);
        guideService.SnapNode(second, main);
        project.GuideNodes.Add(first);
        project.GuideNodes.Add(second);
        GuideStructureService.ReplaceSegments(project, guideService.RebuildSegments(project));
        var existing = Assert.Single(project.GuideSegments);
        existing.ManualData = new SegmentManualData { Description = "原方向人工攻略" };
        var newPosition = new MarkerTrackSnapService().FindNearest(project,
            new GeoCoordinate(23, 113.009), true).Position;
        var preview = new MarkerTrackSnapService().PreviewGuideSegments(project, first, newPosition);
        Assert.Same(existing, Assert.Single(project.GuideSegments));
        Assert.Equal("原方向人工攻略", existing.ManualData.Description);
        Assert.Equal(first.Id, existing.StartNodeId);
        Assert.Equal(second.Id, Assert.Single(preview).StartNodeId);
        Assert.NotEqual(existing.Id, Assert.Single(preview).Id);
        Assert.Equal(new GeoCoordinate(23, 113.003), first.SnappedCoordinate);
    }

    [Fact]
    public void NoMainGuideTrack_RejectsGuideOnlySnapWithoutMutation()
    {
        var (project, _, _) = CreateProject();
        project.MainGuideTrackId = null;
        Assert.Throws<InvalidOperationException>(() => new MarkerTrackSnapService()
            .FindNearest(project, new GeoCoordinate(23, 113), true));
    }

    private static (HikingProject Project, Track Main, Track Ordinary) CreateProject()
    {
        static Track TrackAt(string name, double latitude, double longitude) => new()
        {
            Name = name, Segments = [new TrackSegment { Points =
            [new TrackPoint { Latitude = latitude, Longitude = longitude },
             new TrackPoint { Latitude = latitude, Longitude = longitude + 0.01 }] }]
        };
        var main = TrackAt("攻略轨迹", 23, 113);
        var ordinary = TrackAt("普通轨迹", 24, 114);
        main.RecalculateDistances();
        ordinary.RecalculateDistances();
        var project = new HikingProject();
        project.AddTrack(main);
        project.AddTrack(ordinary, false);
        return (project, main, ordinary);
    }
}
