using HikingGuide.Application.Guides;
using HikingGuide.Application.TrackEditing;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;

namespace HikingGuide.Application.Tests;

public sealed class MainGuideRoleSafetyTests
{
    private static (HikingProject P, Track A, Track B) Fixture()
    {
        Track Make(double lon) => new() { Segments = [new() { Points = [new() { Latitude = 23, Longitude = lon }, new() { Latitude = 23, Longitude = lon+.01 }, new() { Latitude = 23, Longitude = lon+.02 }] }] };
        var a = Make(113); var b = Make(114); a.RecalculateDistances(); b.RecalculateDistances();
        var p = new HikingProject(); p.AddTrack(a); p.AddTrack(b, false); return (p,a,b);
    }
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public void MainTrackOrItsSection_CannotParticipateEvenWhenKeepingSources(bool connect, bool section)
    {
        var (p,a,b) = Fixture(); var before = new TrackSnapService().GetGeometryFingerprint(a);
        Assert.Throws<InvalidOperationException>(() => new TrackMergeService().Prepare(p,
            [new(a.Id, false, section ? a.GetSplitSections()[0].Id : null), new(b.Id,false)], "成果", connect));
        Assert.Equal(before, new TrackSnapService().GetGeometryFingerprint(a)); Assert.Equal(2,p.Tracks.Count);
    }
    [Fact]
    public void MainTrack_AllSplitCombineEntryPointsReject()
    {
        var (p,a,_) = Fixture(); var editor = new TrackEditService();
        Assert.Throws<InvalidOperationException>(() => editor.PrepareSplitSegment(p,a.Id,a.Segments[0].Id,0,.5));
        Assert.Throws<InvalidOperationException>(() => editor.PrepareMergeAdjacentSegments(p,a.Id,a.Segments[0].Id));
        Assert.Throws<InvalidOperationException>(() => editor.PrepareConnectAdjacentSegments(p,a.Id,a.Segments[0].Id));
        Assert.Throws<InvalidOperationException>(() => editor.PrepareCombineSplitSections(p,a.Id,[],false));
    }
    [Fact]
    public void RoleCycle_PreservesIdentityOriginalCoordinatesManualAndMedia_AndUndoRedo()
    {
        var (p,a,b) = Fixture(); var n = new GuideNode { Name="攻略节点", TrackId=a.Id, SourceTrackId=b.Id, OriginalCoordinate=new(23,113.005), SourceElevation=50, ManualData=new() { Notes="人工信息" } };
        new GuideStructureService().SnapNode(n,a); p.GuideNodes.Add(n);
        var image=new MediaAttachment { OwnerId=n.Id, OwnerType=MediaOwnerType.GuideNode }; p.MediaAttachments.Add(image);
        var link=new ExternalMediaReference { OwnerId=n.Id, OwnerType=MediaOwnerType.GuideNode, Url="https://example.com/a.jpg" }; p.ExternalMediaReferences.Add(link);
        var before = n.ExactPosition;
        var demote=MainGuideTrackChange.Prepare(p,null); Assert.Single(p.GuideNodes);
        for (var i=0;i<3;i++)
        {
            demote.Execute(); var w=Assert.Single(p.Waypoints); Assert.Equal(n.Id,w.Id); Assert.Equal(a.Id,w.AssociatedTrackId);
            Assert.Equal(n.OriginalCoordinate,w.PreservedOriginalCoordinate); Assert.Equal("人工信息",w.ManualData!.Notes); Assert.Equal(MediaOwnerType.Waypoint,image.OwnerType);
            var promote=MainGuideTrackChange.Prepare(p,a.Id); promote.Execute();
            var restored=Assert.Single(p.GuideNodes); Assert.Equal(n.Id,restored.Id); Assert.False(restored.IsOrphaned);
            Assert.Equal(n.OriginalCoordinate,restored.OriginalCoordinate); Assert.Equal(50,restored.SourceElevation);
            Assert.Equal("人工信息",restored.ManualData!.Notes); Assert.Equal(MediaOwnerType.GuideNode,link.OwnerType); Assert.Same(image,Assert.Single(p.MediaAttachments));
            promote.Undo(); demote.Undo(); Assert.Same(n,Assert.Single(p.GuideNodes)); Assert.Equal(before,n.ExactPosition); Assert.Empty(p.Waypoints);
        }
    }
    [Fact]
    public void ChangingMain_DemotesOldNodes_PromotesOnlyNewTrackOwnedPoints()
    {
        var (p,a,b)=Fixture(); var n=new GuideNode { TrackId=a.Id,OriginalCoordinate=a.StartPoint!.Coordinate }; new GuideStructureService().SnapNode(n,a); p.GuideNodes.Add(n);
        var owned=new Waypoint { AssociatedTrackId=b.Id,Latitude=23,Longitude=114.005 }; var unrelated=new Waypoint { Latitude=23,Longitude=113 }; p.Waypoints.Add(owned);p.Waypoints.Add(unrelated);
        var plan=MainGuideTrackChange.Prepare(p,b.Id); plan.Execute();
        Assert.Equal(b.Id,p.MainGuideTrackId); Assert.Equal(owned.Id,Assert.Single(p.GuideNodes).Id);
        Assert.Contains(unrelated,p.Waypoints);Assert.Equal(a.Id,p.Waypoints.Single(w=>w.Id==n.Id).AssociatedTrackId);
        plan.Undo();Assert.Same(n,Assert.Single(p.GuideNodes));Assert.Contains(owned,p.Waypoints);Assert.Equal(a.Id,p.MainGuideTrackId);
    }
    [Fact]
    public void PreparedMergeOrSplit_CannotApplyAfterSourceBecomesMain()
    {
        var(p,a,b)=Fixture();p.MainGuideTrackId=null;
        var merge=new TrackMergeService().Prepare(p,[new(a.Id,false),new(b.Id,false)],"成果",false);
        var split=new TrackEditService().PrepareSplitSegment(p,a.Id,a.Segments[0].Id,0,.5);
        p.MainGuideTrackId=a.Id;
        Assert.Throws<InvalidOperationException>(merge.Apply);Assert.Throws<InvalidOperationException>(split.Apply);Assert.Equal(2,p.Tracks.Count);Assert.Single(a.Segments);
    }
    [Fact]
    public void RolePreparationCancellationOrChangedGeometry_DoesNotConvertMarkers()
    {
        var(p,a,_)=Fixture();
        Assert.Throws<OperationCanceledException>(()=>MainGuideTrackChange.Prepare(p,null,new CancellationToken(true)));
        var plan=MainGuideTrackChange.Prepare(p,null);a.Segments[0].Points[0].Longitude+=.001;
        Assert.Throws<InvalidOperationException>(plan.Execute);Assert.Equal(a.Id,p.MainGuideTrackId);
    }
    [Fact]
    public void RoleChange_ArchivesManualSegmentsWithoutDeleting_AndUndoRestoresPlansAndActiveData()
    {
        var(p,a,_)=Fixture(); var structure=new GuideStructureService();
        foreach (var point in new[] { a.StartPoint!,a.EndPoint! })
        { var n=new GuideNode { TrackId=a.Id,OriginalCoordinate=point.Coordinate };structure.SnapNode(n,a);p.GuideNodes.Add(n); }
        var ids=p.GuideNodes.Select(n=>n.Id).ToArray();var key=new HikingGuide.Core.Guides.GuideSegmentKey(a.Id,ids[0],ids[1]);
        p.GuideData.Manual.Segments[key.StorageKey]=new() { SegmentKey=key, RoadCondition="人工路况", PlannedDurationHours=2 };
        p.GuideData.Plans[0].DailyStartNodeIds=[ids[0]];p.GuideData.Plans[0].DepartureTimes[ids[0]]=new(7,30);p.GuideData.Plans[0].DailyNotes[ids[0]]="长备注";
        var change=MainGuideTrackChange.Prepare(p,null);change.Execute();
        Assert.Empty(p.GuideData.Manual.Segments);Assert.Equal("人工路况",Assert.Single(p.GuideData.Manual.ArchivedSegments).Data.RoadCondition);
        change.Undo();Assert.Equal(2,p.GuideData.Manual.Segments[key.StorageKey].PlannedDurationHours);Assert.Empty(p.GuideData.Manual.ArchivedSegments);
        Assert.Equal(new TimeOnly(7,30),p.GuideData.Plans[0].DepartureTimes[ids[0]]);Assert.Equal("长备注",p.GuideData.Plans[0].DailyNotes[ids[0]]);
        change.Execute();p.GuideData.Plans[0].DailyNotes[ids[0]]="后来修改，不能丢弃";
        Assert.Throws<InvalidOperationException>(change.Undo);Assert.Equal("后来修改，不能丢弃",p.GuideData.Plans[0].DailyNotes[ids[0]]);
    }
}
