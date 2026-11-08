using HikingGuide.Application.Guides;
using HikingGuide.Application.TrackEditing;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;
using Xunit;

namespace HikingGuide.Application.Tests;

public sealed class TrackEditServiceTests
{
    [Fact]
    public void Prepare_DoesNotMutateProject_AndMoveUndoRedoRestoresGeometryAndGuide()
    {
        var (project, track) = CreateProject();
        var source = track.Segments[0].Points[1];
        source.SourceElevation = 123;
        source.Timestamp = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var beforeFingerprint = new TrackSnapService().GetGeometryFingerprint(track);
        var beforePosition = project.GuideNodes[1].ExactPosition;
        project.MarkSaved();
        var plan = new TrackEditService().PrepareMovePoint(project, track.Id, source.Id,
            new GeoCoordinate(23.002, 113.01));
        Assert.Equal(beforeFingerprint, new TrackSnapService().GetGeometryFingerprint(track));
        Assert.False(project.IsDirty);
        plan.Apply();
        Assert.Equal(1, track.GeometryRevision);
        Assert.True(project.IsDirty);
        Assert.True(track.Segments[0].Points[1].IsCoordinateEdited);
        Assert.Equal(source.Id, track.Segments[0].Points[1].Id);
        Assert.Equal(123, track.Segments[0].Points[1].SourceElevation);
        Assert.Equal(source.Timestamp, track.Segments[0].Points[1].Timestamp);
        Assert.Equal(beforePosition?.SnappedCoordinate, project.GuideNodes[1].SnappedCoordinate);
        plan.Undo();
        Assert.Equal(0, track.GeometryRevision);
        Assert.Equal(beforeFingerprint, new TrackSnapService().GetGeometryFingerprint(track));
        Assert.Equal(beforePosition, project.GuideNodes[1].ExactPosition);
        plan.Apply();
        Assert.Equal(1, track.GeometryRevision);
    }

    [Fact]
    public void InsertPoint_HasNoInventedSourceElevationOrTimestamp()
    {
        var (project, track) = CreateProject();
        var plan = new TrackEditService().PrepareInsertPoint(project, track.Id, track.Segments[0].Id,
            0, new GeoCoordinate(23.001, 113.005));
        plan.Apply();
        var point = track.Segments[0].Points[1];
        Assert.True(point.IsInsertedByEditor);
        Assert.True(point.IsCoordinateEdited);
        Assert.Null(point.SourceElevation);
        Assert.Null(point.Timestamp);
        Assert.Equal(5, track.GetPointCount());
        plan.Undo();
        Assert.Equal(4, track.GetPointCount());
    }

    [Fact]
    public void ReverseTrack_PreservesNodeLocationAndArchivesDirectionalManualData()
    {
        var (project, track) = CreateProject();
        var oldLength = track.GetTotalLength();
        var oldPositions = project.GuideNodes.ToDictionary(node => node.Id,
            node => node.ExactPosition!);
        var oldSegment = Assert.Single(project.GuideSegments);
        oldSegment.ManualData = new SegmentManualData { Description = "上行危险" };
        var plan = new TrackEditService().PrepareReverseTrack(project, track.Id);
        Assert.Equal(oldLength, plan.Impact.DistanceAfter, 5);
        plan.Apply();
        foreach (var node in project.GuideNodes)
        {
            Assert.Equal(oldPositions[node.Id].SnappedCoordinate, node.SnappedCoordinate);
            Assert.Equal(oldLength - oldPositions[node.Id].DistanceFromTrackStart,
                node.DistanceFromStart!.Value, 5);
        }
        Assert.Contains(project.RetiredGuideSegments, segment => segment.Id == oldSegment.Id);
        Assert.DoesNotContain(project.GuideSegments, segment => segment.Id == oldSegment.Id);
        plan.Undo();
        Assert.Equal(oldSegment.Id, Assert.Single(project.GuideSegments).Id);
        Assert.Equal("上行危险", Assert.Single(project.GuideSegments).ManualData?.Description);
    }

    [Fact]
    public void DeleteSegment_OrphansOnlyNodeOnRemovedGeometry()
    {
        var (project, track) = CreateProject();
        var second = new TrackSegment { Points =
        [
            new TrackPoint { Latitude = 24, Longitude = 114 },
            new TrackPoint { Latitude = 24, Longitude = 114.01 }
        ] };
        track.Segments.Add(second);
        track.RecalculateDistances();
        var removedNode = new GuideNode { TrackId = track.Id,
            OriginalCoordinate = new GeoCoordinate(24, 114.005) };
        new GuideStructureService().SnapNode(removedNode, track);
        project.GuideNodes.Add(removedNode);
        var retained = project.GuideNodes[0];
        var oldCoordinate = retained.SnappedCoordinate;
        var plan = new TrackEditService().PrepareDeleteSegment(project, track.Id, second.Id);
        Assert.False(removedNode.IsOrphaned);
        plan.Apply();
        Assert.True(removedNode.IsOrphaned);
        Assert.Null(removedNode.ExactPosition);
        Assert.False(retained.IsOrphaned);
        Assert.Equal(oldCoordinate, retained.SnappedCoordinate);
        plan.Undo();
        Assert.False(removedNode.IsOrphaned);
    }

    [Fact]
    public void SplitSegment_KeepsLengthAndSnappedGuidePosition()
    {
        var (project, track) = CreateProject();
        project.MainGuideTrackId = null;
        var before = track.GetTotalLength();
        var oldNode = project.GuideNodes[0].SnappedCoordinate;
        var plan = new TrackEditService().PrepareSplitSegment(project, track.Id,
            track.Segments[0].Id, 1, 0.5);
        plan.Apply();
        Assert.Equal(2, track.Segments.Count);
        Assert.Equal(before, track.GetTotalLength(), 5);
        Assert.Equal(oldNode, project.GuideNodes[0].SnappedCoordinate);
        plan.Undo();
        Assert.Single(track.Segments);
    }

    [Fact]
    public void SplitSegment_PreservesExactSnapEvenWhenOriginalMarkerIsFarAway()
    {
        var (project, track) = CreateProject();
        project.MainGuideTrackId = null;
        var node = project.GuideNodes[0];
        var snappedBefore = node.SnappedCoordinate;
        node.OriginalCoordinate = new GeoCoordinate(23.01, 113.02);
        var plan = new TrackEditService().PrepareSplitSegment(project, track.Id,
            track.Segments[0].Id, 0, 0.5);
        plan.Apply();
        Assert.Equal(snappedBefore, node.SnappedCoordinate);
    }

    [Fact]
    public void DeleteRange_CreatesGapAndOrphansOnlyRemovedGuide()
    {
        var (project, track) = CreateProject();
        var middle = new GuideNode { TrackId = track.Id,
            OriginalCoordinate = new GeoCoordinate(23, 113.015) };
        var guide = new GuideStructureService();
        guide.SnapNode(middle, track);
        project.GuideNodes.Add(middle);
        var snap = new TrackSnapService();
        var start = snap.Snap(track, new GeoCoordinate(23, 113.01)).Position!;
        var end = snap.Snap(track, new GeoCoordinate(23, 113.02)).Position!;
        var before = snap.GetGeometryFingerprint(track);
        var plan = new TrackEditService().PrepareDeleteRange(project, new TrackRange(track.Id, start, end));
        Assert.Equal(before, snap.GetGeometryFingerprint(track));
        plan.Apply();
        Assert.Equal(2, track.Segments.Count);
        Assert.True(middle.IsOrphaned);
        Assert.All(project.GuideNodes.Where(node => node.Id != middle.Id), node => Assert.False(node.IsOrphaned));
        Assert.NotEqual(track.Segments[0].EndPoint!.Coordinate,
            track.Segments[1].StartPoint!.Coordinate);
        plan.Undo();
        Assert.Equal(before, snap.GetGeometryFingerprint(track));
        Assert.False(middle.IsOrphaned);
    }

    [Fact]
    public void CropRange_KeepsOnlySelectedGeometryAndOriginalMarkers()
    {
        var (project, track) = CreateProject();
        var snap = new TrackSnapService();
        var start = snap.Snap(track, new GeoCoordinate(23, 113.012)).Position!;
        var end = snap.Snap(track, new GeoCoordinate(23, 113.028)).Position!;
        var oldOriginal = project.GuideNodes[0].OriginalCoordinate;
        var plan = new TrackEditService().PrepareCropRange(project, new TrackRange(track.Id, end, start));
        plan.Apply();
        Assert.True(project.GuideNodes[0].IsOrphaned);
        Assert.Equal(oldOriginal, project.GuideNodes[0].OriginalCoordinate);
        Assert.False(project.GuideNodes[1].IsOrphaned);
        Assert.InRange(track.GetTotalLength(), 1500, 1800);
        plan.Undo();
        Assert.False(project.GuideNodes[0].IsOrphaned);
    }

    [Fact]
    public void AdjacentSegments_RequireRealContinuityOrExplicitConnector()
    {
        var (project, track) = CreateProject();
        project.MainGuideTrackId = null;
        var second = new TrackSegment { Points =
        [
            new TrackPoint { Latitude = 24, Longitude = 114 },
            new TrackPoint { Latitude = 24, Longitude = 114.01 }
        ] };
        track.Segments.Add(second);
        track.RecalculateDistances();
        var service = new TrackEditService();
        Assert.Throws<InvalidOperationException>(() => service.PrepareMergeAdjacentSegments(
            project, track.Id, track.Segments[0].Id));
        var before = track.GetTotalLength();
        var plan = service.PrepareConnectAdjacentSegments(project, track.Id, track.Segments[0].Id);
        Assert.Contains("直线连接", Assert.Single(plan.Warnings));
        plan.Apply();
        Assert.Equal(3, track.Segments.Count);
        Assert.Equal("人工直线连接", track.Segments[1].Name);
        Assert.True(track.GetTotalLength() > before);
        Assert.All(track.Segments[1].Points, point =>
        {
            Assert.True(point.IsInsertedByEditor);
            Assert.Null(point.SourceElevation);
            Assert.Null(point.Timestamp);
        });
        plan.Undo();
        Assert.Equal(2, track.Segments.Count);
    }

    [Fact]
    public void MergeTracks_CreatesIndependentTrackWithoutMovingSourceMarkers()
    {
        var (project, first) = CreateProject();
        project.MainGuideTrackId = null;
        var second = new Track { Name = "第二条", Segments =
        [new TrackSegment { Points =
            [new TrackPoint { Latitude = 24, Longitude = 114 },
             new TrackPoint { Latitude = 24, Longitude = 114.01 }] }] };
        second.RecalculateDistances();
        project.AddTrack(second, false);
        var waypoint = new Waypoint { AssociatedTrackId = first.Id,
            Latitude = 23, Longitude = 113 };
        project.Waypoints.Add(waypoint);
        var main = project.MainGuideTrackId;
        var service = new TrackMergeService();
        var ordered = new[] { new TrackMergeSource(second.Id, true),
            new TrackMergeSource(first.Id, false) };
        var merge = service.Prepare(project, ordered, "合并成果", false);
        Assert.Empty(merge.Connectors);
        merge.Apply();
        Assert.Equal(3, project.Tracks.Count);
        Assert.Equal(2, merge.NewTrack.Segments.Count);
        Assert.Equal(main, project.MainGuideTrackId);
        Assert.Equal(first.Id, waypoint.AssociatedTrackId);
        Assert.Equal(114.01, merge.NewTrack.StartPoint!.Longitude, 2);
        merge.Undo();
        Assert.Equal(2, project.Tracks.Count);
        var connected = service.Prepare(project, ordered, "连接成果", true);
        Assert.Single(connected.Connectors);
        connected.Apply();
        Assert.Equal(3, connected.NewTrack.Segments.Count);
        Assert.True(connected.Connectors[0].LengthMeters > 0);
        Assert.Equal("人工直线连接", connected.NewTrack.Segments[1].Name);
    }

    [Fact]
    public void FiftyEdits_UndoRedoWithoutGeometryDrift_AndOtherProjectStaysUnchanged()
    {
        var (projectA, trackA) = CreateProject();
        var (projectB, trackB) = CreateProject();
        var snap = new TrackSnapService();
        var originalA = snap.GetGeometryFingerprint(trackA);
        var originalB = snap.GetGeometryFingerprint(trackB);
        var pointId = trackA.Segments[0].Points[1].Id;
        var service = new TrackEditService();
        var plans = new List<TrackEditPlan>();
        for (var index = 0; index < 50; index++)
        {
            var plan = service.PrepareMovePoint(projectA, trackA.Id, pointId,
                new GeoCoordinate(23 + (index + 1) * 0.000001, 113.01));
            plan.Apply();
            plans.Add(plan);
        }
        var editedA = snap.GetGeometryFingerprint(trackA);
        Assert.NotEqual(originalA, editedA);
        foreach (var plan in plans.AsEnumerable().Reverse()) plan.Undo();
        Assert.Equal(originalA, snap.GetGeometryFingerprint(trackA));
        Assert.Equal(0, trackA.GeometryRevision);
        foreach (var plan in plans) plan.Apply();
        Assert.Equal(editedA, snap.GetGeometryFingerprint(trackA));
        Assert.Equal(50, trackA.GeometryRevision);
        Assert.Equal(originalB, snap.GetGeometryFingerprint(trackB));
        Assert.Equal(0, trackB.GeometryRevision);
        Assert.Equal(2, projectB.GuideNodes.Count);
    }

    [Fact]
    public void EditPick_UsesWorkingTrackAndOnlyShowsBoundedNeighbors()
    {
        var (_, track) = CreateProject();
        var picker = new TrackPointEditIndex(new TrackEditDisplayProfile(
            NeighborHandleCount: 1, MaxVisibleHandles: 3));
        var click = new GeoCoordinate(23, 113.01);
        var atZoom12 = picker.Pick(track, click, 12)!;
        var atZoom20 = picker.Pick(track, click, 20)!;
        Assert.Equal(track.Segments[0].Points[1].Id, atZoom12.SelectedPoint.PointId);
        Assert.Equal(atZoom12.SelectedPoint.PointId, atZoom20.SelectedPoint.PointId);
        Assert.Equal(3, atZoom20.VisibleHandles.Count);
        Assert.All(atZoom20.VisibleHandles, handle =>
            Assert.Contains(handle.PointId, track.Segments[0].Points.Select(point => point.Id)));
    }

    [Fact]
    public void RangePreview_KeepsSegmentGapsAndDoesNotTouchWorkingTrack()
    {
        var (project, track) = CreateProject();
        track.Segments.Add(new TrackSegment { Points =
        [new TrackPoint { Latitude = 24, Longitude = 114 },
         new TrackPoint { Latitude = 24, Longitude = 114.01 }] });
        track.RecalculateDistances();
        var snap = new TrackSnapService();
        var original = snap.GetGeometryFingerprint(track);
        var start = snap.Snap(track, new GeoCoordinate(23, 113.01)).Position!;
        var end = snap.Snap(track, new GeoCoordinate(24, 114.005)).Position!;
        var preview = new TrackRangePreviewService().Build(track,
            new TrackRange(track.Id, end, start), 14);
        Assert.Equal(2, preview.DisplayPieces.Count);
        Assert.Equal(original, snap.GetGeometryFingerprint(track));
        Assert.Equal(2, project.GuideNodes.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedEdge_UsesOriginalEdgeIdentity_NotNearbyCrossingOrParallelEdge(bool parallel)
    {
        var (project, track) = CreateProject();
        var segment = track.Segments[0];
        segment.Points.Clear();
        if (parallel)
        {
            segment.Points.AddRange([
                new TrackPoint { Latitude = 23, Longitude = 113 },
                new TrackPoint { Latitude = 23, Longitude = 113.02 },
                new TrackPoint { Latitude = 23.00005, Longitude = 113.02 },
                new TrackPoint { Latitude = 23.00005, Longitude = 113 }
            ]);
        }
        else
        {
            segment.Points.AddRange([
                new TrackPoint { Latitude = 23, Longitude = 113 },
                new TrackPoint { Latitude = 23.02, Longitude = 113.02 },
                new TrackPoint { Latitude = 23, Longitude = 113.02 },
                new TrackPoint { Latitude = 23.02, Longitude = 113 }
            ]);
        }
        track.RecalculateDistances();
        project.GuideNodes.Clear();
        project.GuideSegments.Clear();
        var node = new GuideNode { TrackId = track.Id,
            OriginalCoordinate = parallel ? new GeoCoordinate(23, 113.01)
                : new GeoCoordinate(23.01, 113.01) };
        new GuideStructureService().SnapNode(node, track);
        project.GuideNodes.Add(node);
        var oldEdge = node.ExactPosition!.StartPointIndex;
        var changedPointId = segment.Points[oldEdge].Id;
        var moved = new GeoCoordinate(23.001, 113);
        var plan = new TrackEditService().PrepareMovePoint(project, track.Id, changedPointId, moved);
        plan.Apply();
        Assert.False(node.IsOrphaned);
        Assert.Equal(oldEdge, node.ExactPosition!.StartPointIndex);
        Assert.Equal(segment.Id, node.ExactPosition.TrackSegmentId);
        plan.Undo();
        Assert.Equal(oldEdge, node.ExactPosition!.StartPointIndex);
    }

    [Fact]
    public void RepeatedCoordinates_KeepFiniteGeometryThroughInsertSplitReverseAndUndo()
    {
        var (project, track) = CreateProject();
        project.MainGuideTrackId = null;
        var segment = track.Segments[0];
        segment.Points.Insert(1, new TrackPoint { Latitude = 23, Longitude = 113 });
        segment.Points.Insert(2, new TrackPoint { Latitude = 23, Longitude = 113 });
        track.RecalculateDistances();
        var original = new TrackSnapService().GetGeometryFingerprint(track);
        var service = new TrackEditService();
        var inserted = service.PrepareInsertPoint(project, track.Id, segment.Id, 1,
            new GeoCoordinate(23, 113));
        inserted.Apply();
        var split = service.PrepareSplitSegment(project, track.Id, segment.Id, 3, 0.5);
        split.Apply();
        var reverse = service.PrepareReverseTrack(project, track.Id);
        reverse.Apply();
        Assert.True(double.IsFinite(track.GetTotalLength()));
        Assert.All(project.GuideNodes.Where(node => !node.IsOrphaned), node =>
            Assert.True(double.IsFinite(node.DistanceFromStart!.Value)));
        reverse.Undo(); split.Undo(); inserted.Undo();
        Assert.Equal(original, new TrackSnapService().GetGeometryFingerprint(track));
    }

    [Fact]
    public void MainGuideTrackSplit_IsRejectedWithoutGeometryChange_ThenOrdinaryTrackCanSplit()
    {
        var (project, track) = CreateProject();
        var fingerprint = new TrackSnapService().GetGeometryFingerprint(track);
        var service = new TrackEditService();
        var error = Assert.Throws<InvalidOperationException>(() => service.PrepareSplitSegment(
            project, track.Id, track.Segments[0].Id, 1, 0.5));
        Assert.Contains("不可合并、连接或拆分", error.Message);
        Assert.Equal(fingerprint, new TrackSnapService().GetGeometryFingerprint(track));
        project.MainGuideTrackId = null;
        var plan = service.PrepareSplitSegment(project, track.Id, track.Segments[0].Id, 1, 0.5);
        plan.Apply();
        Assert.Equal(2, track.Segments.Count);
    }

    [Fact]
    public void CrossSegmentRangeDeletion_KeepsSeparateEndsAndSupportsUndoRedo()
    {
        var (project, track) = CreateProject();
        var next = new TrackSegment { Points =
        [new TrackPoint { Latitude = 24, Longitude = 114 },
         new TrackPoint { Latitude = 24, Longitude = 114.01 },
         new TrackPoint { Latitude = 24, Longitude = 114.02 }] };
        track.Segments.Add(next);
        track.RecalculateDistances();
        var snap = new TrackSnapService();
        var before = snap.GetGeometryFingerprint(track);
        var start = snap.Snap(track, new GeoCoordinate(23, 113.015)).Position!;
        var end = snap.Snap(track, new GeoCoordinate(24, 114.005)).Position!;
        var plan = new TrackEditService().PrepareDeleteRange(project,
            new TrackRange(track.Id, start, end));
        plan.Apply();
        Assert.Equal(2, track.Segments.Count);
        Assert.NotEqual(track.Segments[0].EndPoint!.Coordinate,
            track.Segments[1].StartPoint!.Coordinate);
        var edited = snap.GetGeometryFingerprint(track);
        plan.Undo();
        Assert.Equal(before, snap.GetGeometryFingerprint(track));
        plan.Apply();
        Assert.Equal(edited, snap.GetGeometryFingerprint(track));
    }

    private static (HikingProject Project, Track Track) CreateProject()
    {
        var track = new Track { Name = "测试轨迹", Segments =
        [
            new TrackSegment { Points =
            [
                new TrackPoint { Latitude = 23, Longitude = 113 },
                new TrackPoint { Latitude = 23, Longitude = 113.01 },
                new TrackPoint { Latitude = 23, Longitude = 113.02 },
                new TrackPoint { Latitude = 23, Longitude = 113.03 }
            ] }
        ] };
        track.RecalculateDistances();
        var project = new HikingProject();
        project.AddTrack(track);
        var guide = new GuideStructureService();
        foreach (var longitude in new[] { 113.005, 113.025 })
        {
            var node = new GuideNode { TrackId = track.Id,
                OriginalCoordinate = new GeoCoordinate(23, longitude) };
            guide.SnapNode(node, track);
            project.GuideNodes.Add(node);
        }
        GuideStructureService.ReplaceSegments(project, guide.RebuildSegments(project));
        return (project, track);
    }
}
