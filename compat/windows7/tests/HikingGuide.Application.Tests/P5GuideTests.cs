using HikingGuide.Application.Guides;
using HikingGuide.Application.TrackFiles;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;

namespace HikingGuide.Application.Tests;

public sealed class P5GuideTests
{
    [Fact]
    public void ImportSessionDefaultsToOrdinaryWaypointsAndMaintainsCheckboxInvariant()
    {
        var parsed = new TrackImportResult();
        parsed.Waypoints.Add(new Waypoint { Latitude = 0, Longitude = 0 });
        var session = ProjectImportSession.Create(parsed, "test.kml", new HikingProject());
        var row = Assert.Single(session.WaypointSelections);
        Assert.True(row.Import);
        Assert.False(row.IsGuideNode);
        row.Import = false;
        row.IsGuideNode = true;
        Assert.True(row.Import);
        row.Import = false;
        Assert.False(row.IsGuideNode);
    }

    [Fact]
    public void PrepareDoesNotMutateProjectAndCommitCreatesSortedSegments()
    {
        var parsed = new TrackImportResult();
        var track = MakeTrack();
        parsed.Tracks.Add(track);
        parsed.Waypoints.Add(new Waypoint { Name = "后点", Latitude = 0, Longitude = 0.009 });
        parsed.Waypoints.Add(new Waypoint { Name = "前点", Latitude = 0, Longitude = 0.001 });
        parsed.Waypoints.Add(new Waypoint { Name = "普通", Latitude = 0, Longitude = 0.004 });
        var project = new HikingProject();
        project.MarkSaved();
        var session = ProjectImportSession.Create(parsed, "test.gpx", project);
        session.SelectedGuideTrackId = track.Id;
        session.WaypointSelections[0].IsGuideNode = true;
        session.WaypointSelections[1].IsGuideNode = true;
        var service = new ProjectImportService();
        var plan = service.Prepare(session, project);
        Assert.Empty(project.Tracks);
        Assert.Empty(project.GuideNodes);
        Assert.False(project.IsDirty);
        service.Apply(plan, project, false);
        Assert.Equal(2, project.GuideNodes.Count);
        Assert.Single(project.Waypoints);
        var segment = Assert.Single(project.GuideSegments);
        Assert.Equal("前点", project.GuideNodes.Single(n => n.Id == segment.StartNodeId).Name);
        Assert.Equal("后点", project.GuideNodes.Single(n => n.Id == segment.EndNodeId).Name);
        Assert.InRange(segment.Distance, 880, 900);
    }

    [Fact]
    public void FarGuideNodeRequiresConfirmationBeforeAnyMutation()
    {
        var parsed = new TrackImportResult();
        parsed.Tracks.Add(MakeTrack());
        parsed.Waypoints.Add(new Waypoint { Latitude = 2, Longitude = 2 });
        var project = new HikingProject();
        project.MarkSaved();
        var session = ProjectImportSession.Create(parsed, "far.kml", project);
        session.SelectedGuideTrackId = parsed.Tracks[0].Id;
        session.WaypointSelections[0].IsGuideNode = true;
        var service = new ProjectImportService();
        var plan = service.Prepare(session, project);
        Assert.Throws<InvalidOperationException>(() => service.Apply(plan, project, false));
        Assert.Empty(project.Tracks);
        Assert.False(project.IsDirty);
    }

    [Fact]
    public void ApplyRollsBackAllCollectionsWhenFailureOccursMidCommit()
    {
        var project = new HikingProject();
        var original = MakeTrack();
        project.AddTrack(original);
        project.MarkSaved();
        var incoming = MakeTrack();
        var plan = new ProjectImportPlan([incoming, incoming], incoming.Id,
            [new Waypoint { Name = "未提交" }], []);

        Assert.Throws<ArgumentException>(() => new ProjectImportService().Apply(plan, project, false));
        Assert.Single(project.Tracks);
        Assert.Same(original, project.ActiveTrack);
        Assert.Empty(project.Waypoints);
        Assert.Empty(project.GuideNodes);
        Assert.Empty(project.GuideSegments);
        Assert.Empty(project.RetiredGuideSegments);
    }

    [Fact]
    public void CustomTypeWithoutNameFailsDuringPreparation()
    {
        var parsed = new TrackImportResult();
        parsed.Waypoints.Add(new Waypoint { Type = WaypointType.Custom });
        var project = new HikingProject();
        var session = ProjectImportSession.Create(parsed, "custom.kml", project);

        Assert.Throws<InvalidOperationException>(() => new ProjectImportService().Prepare(session, project));
        Assert.Empty(project.Waypoints);
    }

    [Fact]
    public void RebuildPreservesSegmentIdAndManualData()
    {
        var project = new HikingProject();
        var track = MakeTrack();
        project.AddTrack(track);
        var guide = new GuideStructureService();
        var first = new GuideNode { Name = "A", OriginalCoordinate = new GeoCoordinate(0, 0.001), TrackId = track.Id };
        var second = new GuideNode { Name = "B", OriginalCoordinate = new GeoCoordinate(0, 0.009), TrackId = track.Id };
        guide.SnapNode(first, track);
        guide.SnapNode(second, track);
        project.GuideNodes.Add(first);
        project.GuideNodes.Add(second);
        var segment = Assert.Single(guide.RebuildSegments(project));
        segment.ManualData = new SegmentManualData { RoadCondition = "石阶" };
        project.GuideSegments.Add(segment);
        var again = Assert.Single(guide.RebuildSegments(project));
        Assert.Equal(segment.Id, again.Id);
        Assert.Equal("石阶", again.ManualData!.RoadCondition);
    }

    [Fact]
    public void OrphanDoesNotBridgeAdjacentValidNodes()
    {
        var project = new HikingProject();
        var track = MakeTrack();
        project.AddTrack(track);
        var guide = new GuideStructureService();
        foreach (var longitude in new[] { 0.001, 0.005, 0.009 })
        {
            var node = new GuideNode { OriginalCoordinate = new GeoCoordinate(0, longitude), TrackId = track.Id };
            guide.SnapNode(node, track);
            project.GuideNodes.Add(node);
        }
        project.GuideNodes[1].IsOrphaned = true;
        Assert.Empty(guide.RebuildSegments(project));
    }

    [Fact]
    public void RemovedAdjacencyRetiresManualDataAndRestoresStableIdsWhenAdjacencyReturns()
    {
        var project = new HikingProject();
        var track = MakeTrack();
        project.AddTrack(track);
        var guide = new GuideStructureService();
        var nodes = new[] { 0.001, 0.005, 0.009 }.Select(longitude =>
        {
            var node = new GuideNode { OriginalCoordinate = new GeoCoordinate(0, longitude), TrackId = track.Id };
            guide.SnapNode(node, track);
            project.GuideNodes.Add(node);
            return node;
        }).ToArray();
        GuideStructureService.ReplaceSegments(project, guide.RebuildSegments(project));
        var original = project.GuideSegments.ToArray();
        original[0].ManualData = new SegmentManualData { RoadCondition = "石阶" };
        project.GuideNodes.Remove(nodes[1]);
        GuideStructureService.ReplaceSegments(project, guide.RebuildSegments(project));
        Assert.Equal(2, project.RetiredGuideSegments.Count);
        Assert.Equal("石阶", project.RetiredGuideSegments.Single(s => s.Id == original[0].Id).ManualData?.RoadCondition);
        project.GuideNodes.Add(nodes[1]);
        GuideStructureService.ReplaceSegments(project, guide.RebuildSegments(project));
        Assert.DoesNotContain(project.RetiredGuideSegments, s => original.Any(o => o.Id == s.Id));
        Assert.Equal(original.Select(s => s.Id).ToHashSet(), project.GuideSegments.Select(s => s.Id).ToHashSet());
        Assert.Equal("石阶", project.GuideSegments.Single(s => s.Id == original[0].Id).ManualData?.RoadCondition);
    }

    [Fact]
    public void FourImportBulkOperationsKeepLegalStates()
    {
        var parsed = new TrackImportResult();
        for (var index = 0; index < 4; index++) parsed.Waypoints.Add(new Waypoint { Latitude = 0, Longitude = index });
        var session = ProjectImportSession.Create(parsed, "bulk.gpx", new HikingProject());
        session.SetAllGuide(true);
        Assert.All(session.WaypointSelections, row => { Assert.True(row.Import); Assert.True(row.IsGuideNode); });
        session.InvertGuide();
        Assert.All(session.WaypointSelections, row => { Assert.True(row.Import); Assert.False(row.IsGuideNode); });
        session.InvertImport();
        Assert.All(session.WaypointSelections, row => { Assert.False(row.Import); Assert.False(row.IsGuideNode); });
        session.SetAllImport(true);
        Assert.All(session.WaypointSelections, row => { Assert.True(row.Import); Assert.False(row.IsGuideNode); });
    }

    [Fact]
    public void MultiTrackImportAttachesGuidesOnlyToExplicitlySelectedTrack()
    {
        var parsed = new TrackImportResult();
        var tracks = Enumerable.Range(0, 3).Select(_ => MakeTrack()).ToArray();
        parsed.Tracks.AddRange(tracks);
        parsed.Waypoints.Add(new Waypoint { Latitude = 0, Longitude = 0.005 });
        var project = new HikingProject();
        var session = ProjectImportSession.Create(parsed, "three.gpx", project);
        session.SelectedGuideTrackId = tracks[1].Id;
        session.WaypointSelections[0].IsGuideNode = true;
        new ProjectImportService().Commit(session, project, false);
        Assert.Equal(3, project.Tracks.Count);
        Assert.Equal(tracks[1].Id, Assert.Single(project.GuideNodes).TrackId);
        Assert.Equal(tracks[1].Id, project.MainGuideTrackId);
        Assert.Equal(tracks[0].Id, project.ActiveTrackId);
    }

    [Fact]
    public void ExistingTrackCanBeGuideTrackWhileImportingNewTrack()
    {
        var project = new HikingProject();
        var existing = MakeTrack();
        project.AddTrack(existing);
        var parsed = new TrackImportResult();
        parsed.Tracks.Add(MakeTrack());
        parsed.Waypoints.Add(new Waypoint { Latitude = 0, Longitude = 0.005 });
        var session = ProjectImportSession.Create(parsed, "mixed.kml", project);
        session.SelectedGuideTrackId = existing.Id;
        session.WaypointSelections[0].IsGuideNode = true;
        new ProjectImportService().Commit(session, project, false);
        Assert.Equal(existing.Id, Assert.Single(project.GuideNodes).TrackId);
        Assert.Equal(2, project.Tracks.Count);
    }

    [Fact]
    public void FarOrdinaryWaypointStaysAtOriginalCoordinateWithoutGuideWarning()
    {
        var parsed = new TrackImportResult();
        parsed.Tracks.Add(MakeTrack());
        parsed.Waypoints.Add(new Waypoint { Latitude = 5, Longitude = 5 });
        var project = new HikingProject();
        var session = ProjectImportSession.Create(parsed, "far.gpx", project);
        new ProjectImportService().Commit(session, project, false);
        Assert.Empty(project.GuideNodes);
        Assert.Equal(5, Assert.Single(project.Waypoints).Latitude);
        Assert.DoesNotContain(new HikingGuide.Application.Projects.ProjectValidator().Validate(project).Warnings,
            warning => warning.Contains("距轨迹"));
    }

    [Fact]
    public void AllExcludedWaypointsDoNotEnterProject()
    {
        var parsed = new TrackImportResult();
        parsed.Waypoints.Add(new Waypoint { Latitude = 1, Longitude = 1 });
        var project = new HikingProject();
        var session = ProjectImportSession.Create(parsed, "only-points.kml", project);
        session.SetAllImport(false);
        new ProjectImportService().Commit(session, project, false);
        Assert.Empty(project.Waypoints);
        Assert.Empty(project.GuideNodes);
    }

    [Fact]
    public void WaypointFileOrderDoesNotChangeGuideMileageOrder()
    {
        static string[] Run(double[] longitudes)
        {
            var parsed = new TrackImportResult();
            parsed.Tracks.Add(MakeTrack());
            foreach (var longitude in longitudes)
                parsed.Waypoints.Add(new Waypoint { Name = longitude.ToString("F3"), Latitude = 0, Longitude = longitude });
            var project = new HikingProject();
            var session = ProjectImportSession.Create(parsed, "order.kml", project);
            session.SelectedGuideTrackId = parsed.Tracks[0].Id;
            session.SetAllGuide(true);
            new ProjectImportService().Commit(session, project, false);
            return new GuideStructureService().SortNodes(project.GuideNodes, project.ActiveTrackId!.Value)
                .Select(node => node.Name!).ToArray();
        }
        Assert.Equal(Run([0.008, 0.002, 0.006]), Run([0.006, 0.002, 0.008]));
    }

    [Fact]
    public void TrackRangeExtractorIncludesProjectedEndpointsAndOriginalIntermediatePoints()
    {
        var track = new Track { Segments = [new TrackSegment { Points = Enumerable.Range(0, 101)
            .Select(i => new TrackPoint { Latitude = 0, Longitude = i * 0.0001 }).ToList() }] };
        var guide = new GuideStructureService();
        var start = guide.SnapNodeToPosition(track, new GeoCoordinate(0, 0.00025));
        var end = guide.SnapNodeToPosition(track, new GeoCoordinate(0, 0.00975));
        var piece = Assert.Single(guide.ExtractRange(track, start, end));
        Assert.Equal(0.00025, piece.First().Longitude, 7);
        Assert.Equal(0.00975, piece.Last().Longitude, 7);
        Assert.Contains(piece, point => Math.Abs(point.Longitude - 0.005) < 1e-10);
    }

    [Fact]
    public void TrackRangeExtractorNeverConnectsSeparateTrackSegments()
    {
        var track = MakeTrack();
        track.Segments.Add(new TrackSegment { Points =
            [new TrackPoint { Latitude = 1, Longitude = 1 }, new TrackPoint { Latitude = 1, Longitude = 1.01 }] });
        var guide = new GuideStructureService();
        var start = guide.SnapNodeToPosition(track, new GeoCoordinate(0, 0.005));
        var end = guide.SnapNodeToPosition(track, new GeoCoordinate(1, 1.005));
        var pieces = guide.ExtractRange(track, start, end);
        Assert.Equal(2, pieces.Count);
        Assert.Equal(0.01, pieces[0].Last().Longitude, 7);
        Assert.Equal(1, pieces[1].First().Longitude, 7);
    }

    private static Track MakeTrack() => new()
    {
        Segments = [new TrackSegment { Points =
            [new TrackPoint { Latitude = 0, Longitude = 0 }, new TrackPoint { Latitude = 0, Longitude = 0.01 }] }]
    };
}
