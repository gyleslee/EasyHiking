using HikingGuide.Application.TrackEditing;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;
using HikingGuide.Application.Guides;
using Xunit;

namespace HikingGuide.Application.Tests;

public sealed class UnifiedTrackMergeTests
{
    private static (HikingProject Project, Track A, Track B) Fixture()
    {
        Track Make(string name, double longitude) => new()
        {
            Name = name, SourceFile = name + ".gpx",
            Segments = Enumerable.Range(0, 2).Select(i => new TrackSegment
            {
                Name = "原分段" + i, SplitSectionId = Guid.NewGuid(),
                Points = [new() { Latitude = 23, Longitude = longitude + i * .02, SourceElevation = 10 },
                    new() { Latitude = 23, Longitude = longitude + i * .02 + .01, SourceElevation = 20 }]
            }).ToList()
        };
        var a = Make("甲轨迹", 113); var b = Make("乙轨迹", 114);
        a.RecalculateDistances(); b.RecalculateDistances();
        var project = new HikingProject(); project.AddTrack(a); project.AddTrack(b, false);
        project.MainGuideTrackId = null;
        return (project, a, b);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedTrackAndSection_UsesOnlySelectedGeometry_AndReverseOrder(bool connect)
    {
        var (p, a, b) = Fixture(); var section = a.GetSplitSections()[1];
        var before = new TrackSnapService().GetGeometryFingerprint(a);
        var plan = new TrackMergeService().Prepare(p,
            [new(a.Id, true, section.Id), new(b.Id, false)], "混合成果", connect);
        Assert.Equal(2, p.Tracks.Count);
        Assert.Equal(3 + (connect ? 1 : 0), plan.NewTrack.Segments.Count);
        Assert.Single(plan.NewTrack.GetSplitSections());
        Assert.Equal(section.EndPoint!.Coordinate, plan.NewTrack.StartPoint!.Coordinate);
        Assert.Equal(section.Length + b.GetTotalLength() + plan.Connectors.Sum(c => c.LengthMeters),
            plan.NewTrack.GetTotalLength(), 6);
        Assert.Equal(before, new TrackSnapService().GetGeometryFingerprint(a));
        plan.Apply(); Assert.Equal(3, p.Tracks.Count); plan.Undo(); Assert.Equal(2, p.Tracks.Count);
        Assert.Equal("甲轨迹.gpx", a.SourceFile);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoSectionsOfSameTrack_CanCreateNewTrack_AndKeepSource(bool connect)
    {
        var (p, a, _) = Fixture(); var sections = a.GetSplitSections();
        var plan = new TrackMergeService().Prepare(p,
            [new(a.Id, false, sections[0].Id), new(a.Id, false, sections[1].Id)], "分段成果", connect);
        Assert.Single(plan.SourceTracks);
        Assert.Single(plan.NewTrack.GetSplitSections());
        plan.Apply(); Assert.Equal(3, p.Tracks.Count);
        Assert.Equal(a.GetTotalLength() + plan.Connectors.Sum(c => c.LengthMeters), plan.NewTrack.GetTotalLength(), 6);
        Assert.All(plan.NewTrack.Segments.SelectMany(s => s.Points), point =>
            Assert.DoesNotContain(a.Segments.SelectMany(s => s.Points), original => original.Id == point.Id));
        plan.Undo(); Assert.Equal(2, p.Tracks.Count); Assert.Null(p.MainGuideTrackId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeleteOriginals_IsReversibleAsOneOperation_PreservesMarkersAndManualData(bool connect)
    {
        var (p, a, b) = Fixture(); p.ActiveTrackId = b.Id;
        var waypoint = new Waypoint { AssociatedTrackId = b.Id, Name = "补给", ManualData = new() { Notes = "人工备注" } };
        var guide = new GuideNode { TrackId = a.Id, SourceTrackId = b.Id, Name = "攻略节点", ManualData = new() { Notes = "节点人工内容" } };
        p.Waypoints.Add(waypoint); p.GuideNodes.Add(guide);
        var manual = p.GuideData.Manual; var media = new MediaAttachment { OwnerId = guide.Id }; p.MediaAttachments.Add(media);
        var plan = new TrackMergeService().Prepare(p, [new(a.Id, false), new(b.Id, true)], "成果", connect, deleteOriginalTracks: true);
        for (var i = 0; i < 3; i++)
        {
            plan.Apply(); Assert.Single(p.Tracks); Assert.Null(p.MainGuideTrackId);
            Assert.Equal(plan.NewTrack.Id, waypoint.AssociatedTrackId); Assert.Null(guide.TrackId); Assert.True(guide.IsOrphaned);
            Assert.Equal(plan.NewTrack.Id, guide.SourceTrackId);
            Assert.Same(manual, p.GuideData.Manual); Assert.Same(media, Assert.Single(p.MediaAttachments));
            Assert.Equal("节点人工内容", guide.ManualData!.Notes); Assert.Equal("人工备注", waypoint.ManualData!.Notes);
            plan.Undo(); Assert.Equal(new[] { a.Id, b.Id }, p.Tracks.Select(t => t.Id));
            Assert.Null(p.MainGuideTrackId); Assert.Equal(b.Id, p.ActiveTrackId);
            Assert.Equal(a.Id, guide.TrackId); Assert.Equal(b.Id, guide.SourceTrackId); Assert.Equal(b.Id, waypoint.AssociatedTrackId);
        }
    }

    [Fact]
    public void DuplicateSection_IsRejectedWithoutMutation()
    {
        var (p, a, _) = Fixture(); var id = a.GetSplitSections()[0].Id;
        Assert.Throws<InvalidOperationException>(() => new TrackMergeService().Prepare(p,
            [new(a.Id, false, id), new(a.Id, true, id)], "成果", false)); Assert.Equal(2, p.Tracks.Count);
    }

    [Fact]
    public void WholeTrackAndItsSection_IsRejectedWithoutDuplicatingGeometry()
    {
        var (p, a, _) = Fixture();
        Assert.Throws<InvalidOperationException>(() => new TrackMergeService().Prepare(p,
            [new(a.Id, false), new(a.Id, false, a.GetSplitSections()[0].Id)], "成果", false));
    }

    [Fact]
    public void ForeignOrDeletedSection_IsRejected()
    {
        var (p, a, b) = Fixture();
        Assert.Throws<InvalidOperationException>(() => new TrackMergeService().Prepare(p,
            [new(a.Id, false, b.GetSplitSections()[0].Id), new(b.Id, false)], "成果", false));
    }

    [Fact]
    public void SourceChangesAfterPreparation_PreventsCreationAndDeletion()
    {
        var (p, a, b) = Fixture();
        var plan = new TrackMergeService().Prepare(p, [new(a.Id, false), new(b.Id, false)], "成果", false, deleteOriginalTracks: true);
        a.Segments[0].Points[0].Longitude += .0001;
        Assert.Throws<InvalidOperationException>(plan.Apply); Assert.Equal(2, p.Tracks.Count); Assert.Null(p.MainGuideTrackId);
    }

    [Fact]
    public void Cancellation_DoesNotCreateOrDeleteTracks()
    {
        var (p, a, b) = Fixture();
        Assert.Throws<OperationCanceledException>(() => new TrackMergeService().Prepare(p,
            [new(a.Id, false), new(b.Id, false)], "成果", true, new CancellationToken(true), true));
        Assert.Equal(2, p.Tracks.Count);
    }

    [Fact]
    public void ModifiedResult_IsNotDiscardedByUndo()
    {
        var (p, a, b) = Fixture(); var plan = new TrackMergeService().Prepare(p,
            [new(a.Id, false), new(b.Id, false)], "成果", false, deleteOriginalTracks: true);
        plan.Apply(); plan.NewTrack.Segments[0].Points[0].Longitude += .01;
        Assert.Throws<InvalidOperationException>(plan.Undo); Assert.Single(p.Tracks);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DeletePartialSections_PreservesUnselectedGeometry_AndRestoresInBothSourceOrders(bool connect, bool reverseSources)
    {
        var (p, a, b) = Fixture(); var sections = a.GetSplitSections();
        var remaining = a.Segments[1]; var beforeFingerprint = new TrackSnapService().GetGeometryFingerprint(a);
        var service = new GuideStructureService();
        var removedNode = new GuideNode { TrackId = a.Id, OriginalCoordinate = sections[0].StartPoint!.Coordinate, ManualData = new() { Notes = "被移除分段的人工数据" } };
        var keptNode = new GuideNode { TrackId = a.Id, OriginalCoordinate = sections[1].StartPoint!.Coordinate };
        service.SnapNode(removedNode, a); service.SnapNode(keptNode, a);
        p.GuideNodes.Add(removedNode); p.GuideNodes.Add(keptNode);
        p.MainGuideTrackId = a.Id;
        var waypoint = new Waypoint { AssociatedTrackId = a.Id, Latitude = remaining.StartPoint!.Latitude, Longitude = remaining.StartPoint.Longitude }; p.Waypoints.Add(waypoint);
        var moved = new Waypoint { AssociatedTrackId = a.Id, Latitude = sections[0].StartPoint!.Latitude, Longitude = sections[0].StartPoint!.Longitude }; p.Waypoints.Add(moved);
        var demote = MainGuideTrackChange.Prepare(p, null); demote.Execute();
        TrackMergeSource[] inputs = [new(a.Id, false, sections[0].Id), new(b.Id, false)];
        if (reverseSources) Array.Reverse(inputs);
        var plan = new TrackMergeService().Prepare(p, inputs, "部分成果", connect, deleteOriginalTracks: true);
        Assert.Equal(2, a.Segments.Count); Assert.Equal(2, p.Tracks.Count);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            plan.Apply(); Assert.Equal(2, p.Tracks.Count); Assert.Contains(a, p.Tracks); Assert.DoesNotContain(b, p.Tracks);
            Assert.Same(remaining, Assert.Single(a.Segments)); Assert.Null(p.MainGuideTrackId);
            Assert.Empty(p.GuideNodes);
            Assert.Equal(a.Id, waypoint.AssociatedTrackId); Assert.Equal("被移除分段的人工数据", removedNode.ManualData!.Notes);
            Assert.Equal(plan.NewTrack.Id, moved.AssociatedTrackId);
            plan.Undo(); Assert.Equal(beforeFingerprint, new TrackSnapService().GetGeometryFingerprint(a));
            Assert.Equal(new[] { a.Id, b.Id }, p.Tracks.Select(track => track.Id));
            Assert.Empty(p.GuideNodes);
            Assert.Equal(a.Id, moved.AssociatedTrackId);
        }
        demote.Undo(); Assert.Equal(a.Id, p.MainGuideTrackId);
        Assert.False(removedNode.IsOrphaned); Assert.False(keptNode.IsOrphaned);
    }

    [Fact]
    public void SelectedAllSections_DeletesEmptyParentRatherThanKeepingEmptyTrack()
    {
        var (p, a, _) = Fixture(); var plan = new TrackMergeService().Prepare(p,
            a.GetSplitSections().Select(section => new TrackMergeSource(a.Id, false, section.Id)).ToArray(),
            "全部分段成果", false, deleteOriginalTracks: true);
        Assert.True(Assert.Single(plan.Removals).WholeTrack);
        plan.Apply(); Assert.DoesNotContain(a, p.Tracks); Assert.Null(p.MainGuideTrackId);
        plan.Undo(); Assert.Contains(a, p.Tracks); Assert.Null(p.MainGuideTrackId);
    }
}
