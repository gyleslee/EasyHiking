using HikingGuide.Application.Guides;
using HikingGuide.Application.TrackFiles;
using HikingGuide.Core.Models;
using Xunit;

namespace HikingGuide.Application.Tests;

public sealed class P52TrackManagementTests
{
    private static Track Track(string name, double latitude)
    {
        var track = new Track { Name = name };
        track.Segments.Add(new TrackSegment
        {
            Points =
            [
                new TrackPoint { Latitude = latitude, Longitude = 113.0 },
                new TrackPoint { Latitude = latitude, Longitude = 113.01 }
            ]
        });
        track.RecalculateDistances();
        return track;
    }

    [Fact]
    public void OrdinaryDeletionKeepsWaypointsAndSourceGuidesAndUndoRestoresOwnership()
    {
        var project = new HikingProject();
        var main = Track("主线", 23.0); var ordinary = Track("备用", 23.1);
        project.AddTrack(main); project.AddTrack(ordinary); project.MainGuideTrackId = main.Id;
        var markers = Enumerable.Range(0, 5).Select(i => new Waypoint
        {
            Name = $"标注{i}", Latitude = 23.1, Longitude = 113.001 + i * 0.001,
            AssociatedTrackId = ordinary.Id
        }).ToArray();
        foreach (var marker in markers) project.Waypoints.Add(marker);
        var guide = markers[0].ToGuideNode();
        guide.TrackId = main.Id;
        guide.SourceTrackId = ordinary.Id;
        new GuideStructureService().SnapNode(guide, main);
        project.Waypoints.Remove(markers[0]); project.GuideNodes.Add(guide);
        var deletion = new TrackManagementService().PrepareDeletion(project, ordinary.Id);
        Assert.Equal(4, deletion.Impact.DirectWaypoints);
        Assert.Equal(1, deletion.Impact.SourceGuideNodes);
        deletion.Execute();
        Assert.DoesNotContain(ordinary, project.Tracks);
        Assert.All(project.Waypoints, marker => Assert.Null(marker.AssociatedTrackId));
        Assert.Equal(main.Id, guide.TrackId);
        Assert.Null(guide.SourceTrackId);
        Assert.Equal("备用", guide.SourceTrackName);
        Assert.Null(guide.ToWaypoint(project).AssociatedTrackId);
        deletion.Undo();
        Assert.Contains(ordinary, project.Tracks);
        Assert.All(project.Waypoints, marker => Assert.Equal(ordinary.Id, marker.AssociatedTrackId));
        Assert.Equal(ordinary.Id, guide.SourceTrackId);
        Assert.Equal(ordinary.Id, guide.ToWaypoint(project).AssociatedTrackId);
        deletion.Execute();
        Assert.Equal(4, project.Waypoints.Count);
    }

    [Fact]
    public void MainDeletionOrphansGuidesWithoutDeletingContentAndUndoRestoresPositions()
    {
        var project = new HikingProject();
        var main = Track("主线", 23.0); project.AddTrack(main); project.MainGuideTrackId = main.Id;
        var node = new GuideNode { Name = "营地", OriginalCoordinate = new(23.0, 113.005) };
        new GuideStructureService().SnapNode(node, main); project.GuideNodes.Add(node);
        var originalPosition = node.ExactPosition;
        var deletion = new TrackManagementService().PrepareDeletion(project, main.Id);
        deletion.Execute();
        Assert.Null(project.MainGuideTrackId);
        Assert.Single(project.GuideNodes);
        Assert.True(node.IsOrphaned);
        Assert.Null(node.TrackId);
        Assert.Null(node.SnappedCoordinate);
        deletion.Undo();
        Assert.Equal(main.Id, project.MainGuideTrackId);
        Assert.Equal(originalPosition, node.ExactPosition);
        Assert.False(node.IsOrphaned);
    }

    [Fact]
    public void ChangingMainDoesNotFollowSelectedTrackAndIsUndoable()
    {
        var project = new HikingProject();
        var first = Track("主线", 23.0); var second = Track("备用", 23.1);
        project.AddTrack(first); project.AddTrack(second); project.MainGuideTrackId = first.Id;
        var node = new GuideNode { OriginalCoordinate = new(23.0, 113.005) };
        new GuideStructureService().SnapNode(node, first); project.GuideNodes.Add(node);
        project.ActiveTrackId = second.Id;
        Assert.Equal(first.Id, project.MainGuideTrackId);
        var change = MainGuideTrackChange.Prepare(project, second.Id);
        change.Execute();
        Assert.DoesNotContain(node, project.GuideNodes);
        Assert.Equal(first.Id, Assert.Single(project.Waypoints).AssociatedTrackId);
        Assert.Equal(second.Id, project.MainGuideTrackId);
        change.Undo();
        Assert.Equal(first.Id, node.TrackId);
        Assert.Equal(first.Id, project.MainGuideTrackId);
        Assert.Equal(second.Id, project.ActiveTrackId);
    }

    [Fact]
    public void MultiTrackImportGroupsWaypointsAndMovesSelectedGuideToMain()
    {
        var parsed = new TrackImportResult();
        var tracks = new[] { Track("主线", 23.0), Track("备用", 23.1), Track("下撤", 23.2) };
        parsed.Tracks.AddRange(tracks);
        foreach (var track in tracks)
            for (var index = 0; index < 2; index++)
                parsed.Waypoints.Add(new Waypoint
                {
                    Name = $"{track.Name}{index}", Latitude = 23.0, Longitude = 113.005,
                    AssociatedTrackId = track.Id
                });
        var project = new HikingProject();
        var session = ProjectImportSession.Create(parsed, "multi.kml", project);
        session.SelectedGuideTrackId = tracks[0].Id;
        session.WaypointSelections[2].IsGuideNode = true;
        new ProjectImportService().Commit(session, project, true);
        Assert.Equal(3, project.Tracks.Count);
        Assert.Equal(tracks[0].Id, project.MainGuideTrackId);
        var guide = Assert.Single(project.GuideNodes);
        Assert.Equal(tracks[0].Id, guide.TrackId);
        Assert.Equal(tracks[1].Id, guide.SourceTrackId);
        Assert.Equal(2, project.Waypoints.Count(point => point.AssociatedTrackId == tracks[0].Id));
        Assert.Equal(1, project.Waypoints.Count(point => point.AssociatedTrackId == tracks[1].Id));
        Assert.Equal(2, project.Waypoints.Count(point => point.AssociatedTrackId == tracks[2].Id));
    }

    [Fact]
    public void ImportCanExplicitlyLeaveProjectWithoutMainAndGuideSelectionsAreRejected()
    {
        var parsed = new TrackImportResult();
        var track = Track("普通线", 23.0); parsed.Tracks.Add(track);
        parsed.Waypoints.Add(new Waypoint { Latitude = 23, Longitude = 113.005, AssociatedTrackId = track.Id });
        var project = new HikingProject();
        var session = ProjectImportSession.Create(parsed, "ordinary.gpx", project);
        session.SelectedGuideTrackId = null;
        var service = new ProjectImportService();
        session.WaypointSelections[0].IsGuideNode = true;
        Assert.Throws<InvalidOperationException>(() => service.Prepare(session, project));
        session.WaypointSelections[0].IsGuideNode = false;
        service.Commit(session, project, true);
        Assert.Null(project.MainGuideTrackId);
        Assert.Equal(track.Id, Assert.Single(project.Waypoints).AssociatedTrackId);
    }
}
