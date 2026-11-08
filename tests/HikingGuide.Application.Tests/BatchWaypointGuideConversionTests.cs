using HikingGuide.Application.Guides;
using HikingGuide.Core.Models;

namespace HikingGuide.Application.Tests;

public sealed class BatchWaypointGuideConversionTests
{
    [Fact]
    public void ConvertsSelectedWaypointsInOnePlanAndUndoRestoresMediaAndManualSegments()
    {
        var project = new HikingProject();
        var main = MakeTrack("主线", 23);
        var source = MakeTrack("来源线", 23.1);
        project.AddTrack(main);
        project.AddTrack(source);
        project.MainGuideTrackId = main.Id;
        var guides = new[]
        {
            new GuideNode { Name = "起点", OriginalCoordinate = new(23, 113.001) },
            new GuideNode { Name = "终点", OriginalCoordinate = new(23, 113.009) }
        };
        var service = new GuideStructureService();
        foreach (var guide in guides) { service.SnapNode(guide, main); project.GuideNodes.Add(guide); }
        var originalSegment = Assert.Single(service.RebuildSegments(project));
        originalSegment.ManualData = new SegmentManualData { Description = "人工填写的路况" };
        project.GuideSegments.Add(originalSegment);
        var named = new Waypoint { Name = "图片节点", Latitude = 23, Longitude = 113.004,
            AssociatedTrackId = source.Id, ManualData = new ManualGuideData { } };
        var unnamed = new Waypoint { Latitude = 23, Longitude = 113.006 };
        var untouched = new Waypoint { Name = "不转换", Latitude = 23.1, Longitude = 113.003 };
        project.Waypoints.Add(named); project.Waypoints.Add(unnamed); project.Waypoints.Add(untouched);
        var attachment = new MediaAttachment { OwnerId = named.Id, OwnerType = MediaOwnerType.Waypoint };
        project.MediaAttachments.Add(attachment);
        var external = new ExternalMediaReference { OwnerId = named.Id, OwnerType = MediaOwnerType.Waypoint,
            Url = "https://example.com/photo.jpg" };
        project.ExternalMediaReferences.Add(external);

        var plan = new BatchWaypointGuideConversion().Prepare(project, [named, unnamed]);
        Assert.Equal(2, plan.Count);
        Assert.Equal(0, plan.FarCount(100));
        Assert.Equal(2, plan.NewNodes.Count);
        Assert.Equal(2, project.GuideNodes.Count);
        Assert.Single(project.GuideSegments);
        Assert.Equal("人工填写的路况", originalSegment.ManualData.Description);

        plan.Apply();
        Assert.Single(project.Waypoints);
        Assert.Same(untouched, project.Waypoints[0]);
        Assert.Equal(4, project.GuideNodes.Count);
        Assert.Equal("图片节点", project.GuideNodes.First(node => node.Id == named.Id).Name);
        Assert.Equal(source.Id, project.GuideNodes.First(node => node.Id == named.Id).SourceTrackId);
        Assert.Equal(MediaOwnerType.GuideNode, attachment.OwnerType);
        Assert.Equal(MediaOwnerType.GuideNode, external.OwnerType);
        Assert.Equal("人工填写的路况", project.RetiredGuideSegments.Single(segment => segment.Id == originalSegment.Id).ManualData?.Description);

        plan.Undo();
        Assert.Equal(3, project.Waypoints.Count);
        Assert.Equal(2, project.GuideNodes.Count);
        Assert.Same(originalSegment, Assert.Single(project.GuideSegments));
        Assert.Empty(project.RetiredGuideSegments);
        Assert.Equal(MediaOwnerType.Waypoint, attachment.OwnerType);
        Assert.Equal(MediaOwnerType.Waypoint, external.OwnerType);
        plan.Apply();
        Assert.Equal(4, project.GuideNodes.Count);
    }

    [Fact]
    public void RequiresMainGuideTrackAndDoesNotAlterProjectOnPreparationFailure()
    {
        var project = new HikingProject();
        var track = MakeTrack("普通线", 23);
        project.AddTrack(track);
        project.MainGuideTrackId = null;
        var point = new Waypoint { Latitude = 23, Longitude = 113.005 };
        project.Waypoints.Add(point);
        Assert.Throws<InvalidOperationException>(() => new BatchWaypointGuideConversion().Prepare(project, [point]));
        Assert.Contains(point, project.Waypoints);
        Assert.Empty(project.GuideNodes);
    }

    [Fact]
    public void ConvertsSelectedGuideNodesToOrdinaryAndUndoRestoresSegmentsAndMedia()
    {
        var project = new HikingProject();
        var main = MakeTrack("主线", 23);
        project.AddTrack(main);
        project.MainGuideTrackId = main.Id;
        var guides = new[] { 0.001, 0.005, 0.009 }.Select((longitude, index) =>
            new GuideNode { Name = index == 1 ? "照片节点" : null,
                OriginalCoordinate = new(23, 113 + longitude),
                ManualData = index == 1 ? new ManualGuideData() : null }).ToArray();
        var structure = new GuideStructureService();
        foreach (var guide in guides) { structure.SnapNode(guide, main); project.GuideNodes.Add(guide); }
        foreach (var segment in structure.RebuildSegments(project)) project.GuideSegments.Add(segment);
        var oldSegments = project.GuideSegments.ToArray();
        oldSegments[0].ManualData = new SegmentManualData { Description = "人工内容" };
        var attachment = new MediaAttachment { OwnerId = guides[1].Id, OwnerType = MediaOwnerType.GuideNode };
        project.MediaAttachments.Add(attachment);
        var plan = new BatchGuideWaypointConversion().Prepare(project, [guides[1], guides[2]]);
        Assert.Equal(2, project.GuideSegments.Count);
        plan.Apply();
        Assert.Single(project.GuideNodes);
        Assert.Equal(2, project.Waypoints.Count);
        var photo = project.Waypoints.Single(point => point.Id == guides[1].Id);
        Assert.Equal("照片节点", photo.Name);
        Assert.NotNull(photo.ManualData);
        Assert.Equal(main.Id, photo.AssociatedTrackId);
        Assert.Equal(MediaOwnerType.Waypoint, attachment.OwnerType);
        Assert.Empty(project.GuideSegments);
        Assert.Contains(project.RetiredGuideSegments, segment => segment.ManualData?.Description == "人工内容");
        plan.Undo();
        Assert.Empty(project.Waypoints);
        Assert.Equal(3, project.GuideNodes.Count);
        Assert.Equal(oldSegments, project.GuideSegments);
        Assert.Equal(MediaOwnerType.GuideNode, attachment.OwnerType);
        plan.Apply();
        Assert.Equal(2, project.Waypoints.Count);
    }

    [Fact]
    public void SegmentLabelUsesNodeNamesAndUnnamedFallback()
    {
        var project = new HikingProject();
        var photo = new GuideNode { Name = "照片节点" };
        var unnamed = new GuideNode { Name = "  " };
        project.GuideNodes.Add(photo);
        project.GuideNodes.Add(unnamed);
        var segment = new GuideSegment { StartNodeId = photo.Id, EndNodeId = unnamed.Id,
            StartDistance = 1000, EndDistance = 2234 };
        var label = GuideSegmentDisplayFormatter.Format(project, segment);
        Assert.StartsWith("照片节点 → 未命名节点 ", label);
        Assert.EndsWith(" km", label);
        Assert.Contains("1.23", label);
    }

    private static Track MakeTrack(string name, double latitude)
    {
        var track = new Track { Name = name };
        track.Segments.Add(new TrackSegment { Points =
        [
            new TrackPoint { Latitude = latitude, Longitude = 113 },
            new TrackPoint { Latitude = latitude, Longitude = 113.01 }
        ] });
        track.RecalculateDistances();
        return track;
    }
}
