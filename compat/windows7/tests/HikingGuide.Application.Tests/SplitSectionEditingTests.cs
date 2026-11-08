using HikingGuide.Application.TrackEditing;
using HikingGuide.Application.Guides;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;
using Xunit;

namespace HikingGuide.Application.Tests;

public sealed class SplitSectionEditingTests
{
    [Fact]
    public void CombinedSections_GetSequentialNames_AndRetainUserDescriptions()
    {
        var (project, track) = BuildGappedProject();
        track.Segments[0].SplitSectionDescription = "第一段人工说明";
        track.Segments[1].SplitSectionDescription = "第二段人工说明";
        var original = track.GetSplitSections();
        new TrackEditService().PrepareCombineSplitSections(project, track.Id,
            [new(original[0].Id, false), new(original[1].Id, false)], false).Apply();
        var first = track.GetSplitSections()[0];
        Assert.Equal("合并后的分段1", first.Name);
        Assert.All(first.GeometryParts, part =>
        {
            Assert.Contains("第一段人工说明", part.SplitSectionDescription);
            Assert.Contains("第二段人工说明", part.SplitSectionDescription);
        });
        var remaining = track.GetSplitSections();
        new TrackEditService().PrepareCombineSplitSections(project, track.Id,
            [new(remaining[0].Id, false), new(remaining[1].Id, false)], false).Apply();
        Assert.Equal("合并后的分段2", Assert.Single(track.GetSplitSections()).Name);
    }

    [Fact]
    public void GuideSegmentCustomName_OverridesOnlyDisplayName_NotDistance()
    {
        var project = new HikingProject();
        var start = new GuideNode { Name = "起点" };
        var end = new GuideNode { Name = "终点" };
        project.GuideNodes.Add(start);
        project.GuideNodes.Add(end);
        var segment = new GuideSegment { StartNodeId = start.Id, EndNodeId = end.Id,
            StartDistance = 1000, EndDistance = 3500 };
        Assert.Equal("起点 → 终点 2.50 km", GuideSegmentDisplayFormatter.Format(project, segment));
        segment.CustomName = "山脊路段";
        Assert.Equal("山脊路段 2.50 km", GuideSegmentDisplayFormatter.Format(project, segment));
        Assert.Equal(2500, segment.Distance);
    }

    [Fact]
    public void Merge_WithGaps_CreatesOneLogicalSectionWithoutInventedGeometry_AndUndoRestores()
    {
        var (project, track) = BuildGappedProject();
        var beforeLength = track.GetTotalLength();
        var sources = track.GetSplitSections().Select(section => new SplitSectionSource(section.Id, false)).ToArray();
        project.MarkSaved();
        var plan = new TrackEditService().PrepareCombineSplitSections(project, track.Id, sources, false);
        Assert.False(project.IsDirty);
        plan.Apply();
        var merged = Assert.Single(track.GetSplitSections());
        Assert.Equal(3, merged.GeometryParts.Count);
        Assert.Equal(beforeLength, track.GetTotalLength(), 5);
        Assert.NotEqual(merged.GeometryParts[0].EndPoint!.Coordinate,
            merged.GeometryParts[1].StartPoint!.Coordinate);
        Assert.NotEqual(merged.GeometryParts[1].EndPoint!.Coordinate,
            merged.GeometryParts[2].StartPoint!.Coordinate);
        plan.Undo();
        Assert.Equal(3, track.GetSplitSections().Count);
        Assert.Equal(beforeLength, track.GetTotalLength(), 5);
        plan.Apply();
        Assert.Equal(merged.Id, Assert.Single(track.GetSplitSections()).Id);
    }

    [Fact]
    public void Connect_UsesPanelOrderAndDirection_WithExplicitUnattributedConnectors()
    {
        var (project, track) = BuildGappedProject();
        var original = track.GetSplitSections();
        var aEnd = original[0].EndPoint!.Coordinate;
        var cEnd = original[2].EndPoint!.Coordinate;
        var bStart = original[1].StartPoint!.Coordinate;
        var beforeLength = track.GetTotalLength();
        var plan = new TrackEditService().PrepareCombineSplitSections(project, track.Id,
            [new(original[0].Id, false), new(original[2].Id, true), new(original[1].Id, false)], true);
        plan.Apply();
        var combined = Assert.Single(track.GetSplitSections());
        var connectors = combined.GeometryParts.Where(part =>
            part.OriginalAttributes.GetValueOrDefault("hg:editorProvenance") == "人工直线连接").ToArray();
        Assert.Equal(2, connectors.Length);
        Assert.Equal(aEnd, connectors[0].StartPoint!.Coordinate);
        Assert.Equal(cEnd, connectors[0].EndPoint!.Coordinate);
        Assert.Equal(bStart, connectors[1].EndPoint!.Coordinate);
        Assert.All(connectors.SelectMany(part => part.Points), point =>
        {
            Assert.True(point.IsInsertedByEditor);
            Assert.Null(point.SourceElevation);
            Assert.Null(point.Timestamp);
        });
        var expected = beforeLength + connectors.Sum(part => part.Length);
        Assert.Equal(expected, track.GetTotalLength(), 5);
        plan.Undo();
        Assert.Equal(3, track.GetSplitSections().Count);
        Assert.DoesNotContain(track.Segments, part =>
            part.OriginalAttributes.GetValueOrDefault("hg:editorProvenance") == "人工直线连接");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MainGuideTrack_ReordersGuidesStructurallyAndPreservesManualData(bool connect)
    {
        var (project, track) = BuildGappedProject();
        project.MainGuideTrackId = track.Id;
        var guideService = new GuideStructureService();
        foreach (var part in track.Segments)
        {
            var node = new GuideNode { TrackId = track.Id, Name = "人工节点",
                OriginalCoordinate = new GeoCoordinate(23,
                    (part.StartPoint!.Longitude + part.EndPoint!.Longitude) / 2) };
            guideService.SnapNode(node, track);
            project.GuideNodes.Add(node);
        }
        GuideStructureService.ReplaceSegments(project, guideService.RebuildSegments(project));
        var oldPositions = project.GuideNodes.ToDictionary(node => node.Id, node => node.ExactPosition);
        var manual = project.GuideSegments[0];
        manual.ManualData = new SegmentManualData { Description = "保留人工描述" };
        manual.CustomName = "人工攻略分段名";
        var sections = track.GetSplitSections();
        var sources = new[]
        {
            new SplitSectionSource(sections[0].Id, false),
            new SplitSectionSource(sections[2].Id, true),
            new SplitSectionSource(sections[1].Id, false)
        };
        Assert.Throws<InvalidOperationException>(() => new TrackEditService().PrepareCombineSplitSections(project, track.Id, sources, connect));
        var demote = MainGuideTrackChange.Prepare(project, null); demote.Execute();
        Assert.Empty(project.GuideNodes); Assert.Equal(3, project.Waypoints.Count);
        var plan = new TrackEditService().PrepareCombineSplitSections(project, track.Id, sources, connect);
        plan.Apply();
        var promote = MainGuideTrackChange.Prepare(project, track.Id); promote.Execute();
        foreach (var node in project.GuideNodes)
        {
            Assert.False(node.IsOrphaned);
            Assert.Equal(oldPositions[node.Id]!.SnappedCoordinate, node.SnappedCoordinate);
            var exact = node.ExactPosition!;
            Assert.Equal(track.Segments[exact.SegmentIndex].Id, exact.TrackSegmentId);
            var expected = track.Segments.Take(exact.SegmentIndex).Sum(part => part.Length) +
                track.Segments[exact.SegmentIndex].Points.Take(exact.StartPointIndex)
                    .Zip(track.Segments[exact.SegmentIndex].Points.Skip(1),
                        (a, b) => GeoDistanceCalculator.Instance.CalculateDistance(a.Coordinate, b.Coordinate)).Sum() +
                GeoDistanceCalculator.Instance.CalculateDistance(
                    track.Segments[exact.SegmentIndex].Points[exact.StartPointIndex].Coordinate,
                    track.Segments[exact.SegmentIndex].Points[exact.EndPointIndex].Coordinate) * exact.Fraction;
            Assert.Equal(expected, exact.DistanceFromTrackStart, 4);
        }
        Assert.Contains(project.GuideSegments.Concat(project.RetiredGuideSegments), segment =>
            segment.Id == manual.Id && segment.ManualData?.Description == "保留人工描述" &&
            segment.CustomName == "人工攻略分段名");
        promote.Undo(); plan.Undo(); demote.Undo();
        Assert.All(project.GuideNodes, node => Assert.Equal(oldPositions[node.Id], node.ExactPosition));
        Assert.Equal("保留人工描述", project.GuideSegments[0].ManualData?.Description);
        Assert.Equal("人工攻略分段名", project.GuideSegments[0].CustomName);
    }

    private static (HikingProject Project, Track Track) BuildGappedProject()
    {
        var track = new Track { Name = "三段有间隙轨迹" };
        foreach (var longitude in new[] { 113.0, 113.02, 113.05 })
            track.Segments.Add(new TrackSegment { Points =
            [
                new TrackPoint { Latitude = 23, Longitude = longitude },
                new TrackPoint { Latitude = 23, Longitude = longitude + 0.005 }
            ] });
        track.RecalculateDistances();
        var project = new HikingProject();
        project.AddTrack(track);
        project.MainGuideTrackId = null;
        return (project, track);
    }
}
