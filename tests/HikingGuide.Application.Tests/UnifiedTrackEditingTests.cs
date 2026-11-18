using HikingGuide.Application.TrackEditing;
using HikingGuide.Application.Maps;
using HikingGuide.Application.Guides;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;
using Xunit;

namespace HikingGuide.Application.Tests;

public sealed class UnifiedTrackEditingTests
{
    private static (HikingProject Project, Track Track) Fixture(int sections = 3)
    {
        var t = new Track { Name = "编辑测试", Segments = Enumerable.Range(0, sections).Select(s => new TrackSegment
        { SplitSectionId = Guid.NewGuid(), SplitSectionName = $"分段{s + 1}", Points = Enumerable.Range(0, 6).Select(i => new TrackPoint
            { Latitude = 23, Longitude = 113 + s * .01 + i * .001, SourceElevation = i * 5, OriginalAttributes = new() { ["测试"] = "保留" } }).ToList() }).ToList() };
        t.RecalculateDistances(); var p = new HikingProject(); p.AddTrack(t, false); p.MarkSaved(); return (p, t);
    }
    private static string Fingerprint(Track t) => new TrackSnapService().GetGeometryFingerprint(t);
    private static TrackPosition Position(Track t, int part, int point, double fraction = 0)
    {
        var s = t.Segments[part];var edge = Math.Min(point, s.Points.Count - 2);
        var f = point == s.Points.Count - 1 ? 1 : fraction;
        var distance = s.Points[edge].DistanceFromStart + (s.Points[edge + 1].DistanceFromStart - s.Points[edge].DistanceFromStart) * f;
        return new() { TrackId = t.Id, TrackSegmentId = s.Id, SegmentIndex = part, StartPointIndex = edge, EndPointIndex = edge + 1,
            Fraction = f, DistanceFromTrackStart = distance, DistanceFromSegmentStart = distance - s.Points[0].DistanceFromStart,
            TrackGeometryFingerprint = Fingerprint(t) };
    }
    [Fact]
    public void MultiPointDelete_KeepsUnselectedPointsMetadataAndSupportsUndoRedo()
    {
        var (p,t)=Fixture();var before=Fingerprint(t);var points=t.Segments[1].Points.ToArray();
        var plan=new TrackEditService().PrepareDeletePoints(p,t.Id,new HashSet<Guid>{points[1].Id,points[3].Id});
        Assert.Equal(before,Fingerprint(t));plan.Apply();Assert.Equal(new[]{points[0].Id,points[2].Id,points[4].Id,points[5].Id},t.Segments[1].Points.Select(x=>x.Id));
        Assert.Equal("保留",t.Segments[1].Points[1].OriginalAttributes["测试"]);plan.Undo();Assert.Equal(before,Fingerprint(t));plan.Apply();Assert.Equal(4,t.Segments[1].Points.Count);
    }
    [Fact]
    public void InvalidPointDeletion_RejectsWholeBatchBeforeMutation()
    {
        var(p,t)=Fixture();var before=Fingerprint(t);var ids=t.Segments[0].Points.Skip(1).Select(x=>x.Id).Append(t.Segments[1].Points[1].Id).ToHashSet();
        Assert.Throws<InvalidOperationException>(()=>new TrackEditService().PrepareDeletePoints(p,t.Id,ids));Assert.Equal(before,Fingerprint(t));Assert.False(p.IsDirty);
    }
    [Fact]
    public void ReverseSelectedSections_LeavesOtherSectionAndSectionOrderUnchanged()
    {
        var(p,t)=Fixture();var before=Fingerprint(t);var original=t.Segments.Select(s=>s.Points.Select(x=>x.Id).ToArray()).ToArray();
        var plan=new TrackEditService().PrepareReverseSections(p,t.Id,t.GetSplitSections().Where((_,i)=>i!=1).Select(s=>s.Id).ToHashSet());
        plan.Apply();Assert.Equal(original[0].Reverse(),t.Segments[0].Points.Select(x=>x.Id));Assert.Equal(original[1],t.Segments[1].Points.Select(x=>x.Id));Assert.Equal(original[2].Reverse(),t.Segments[2].Points.Select(x=>x.Id));plan.Undo();Assert.Equal(before,Fingerprint(t));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CutAndJoin_PreservesBoundaryPointsAndCreatesContinuousGeometry(bool reversedPickOrder)
    {
        var(p,t)=Fixture();var before=Fingerprint(t);var old=t.Segments[1].Points.ToArray();var a=Position(t,1,1);var b=Position(t,1,4);
        var plan=new TrackEditService().PrepareCutAndJoin(p,new(t.Id,reversedPickOrder?b:a,reversedPickOrder?a:b),t.GetSplitSections()[1].Id);
        plan.Apply();Assert.Equal(3,t.Segments.Count);Assert.Equal(new[]{old[0].Id,old[1].Id,old[4].Id,old[5].Id},t.Segments[1].Points.Select(x=>x.Id));plan.Undo();Assert.Equal(before,Fingerprint(t));
    }
    [Fact]
    public void CutAndJoin_InterpolatesTwoBoundaryPointsAtEndsOfRemovedInterior()
    {
        var(p,t)=Fixture(1);var plan=new TrackEditService().PrepareCutAndJoin(p,new(t.Id,Position(t,0,1,.5),Position(t,0,3,.5)));
        plan.Apply();Assert.Single(t.Segments);Assert.Equal(new[]{113,113.001,113.0015,113.0035,113.004,113.005},t.Segments[0].Points.Select(x=>x.Longitude),new DoubleComparer());
    }
    private sealed class DoubleComparer : IEqualityComparer<double>{public bool Equals(double a,double b)=>Math.Abs(a-b)<1e-8;public int GetHashCode(double d)=>0;}
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void TrimSection_OnlyChangesChosenSection(bool before)
    {
        var(p,t)=Fixture();var fingerprint=Fingerprint(t);var left=t.Segments[0];var right=t.Segments[2];var old=t.Segments[1].Points.ToArray();
        var plan=new TrackEditService().PrepareTrimAtPoint(p,Position(t,1,2),before,t.GetSplitSections()[1].Id);plan.Apply();
        Assert.Same(left,t.Segments[0]);Assert.Same(right,t.Segments[2]);Assert.Equal(before?old.Skip(2).Select(x=>x.Id):old.Take(3).Select(x=>x.Id),t.Segments[1].Points.Select(x=>x.Id));plan.Undo();Assert.Equal(fingerprint,Fingerprint(t));
    }
    [Fact]
    public void CutSection_RejectsCrossSectionEndpoints()
    {
        var(p,t)=Fixture();Assert.Throws<InvalidOperationException>(()=>new TrackEditService().PrepareCutAndJoin(p,new(t.Id,Position(t,0,1),Position(t,1,4)),t.GetSplitSections()[0].Id));Assert.False(p.IsDirty);
    }
    [Fact]
    public void BatchDelete_AllSectionsDeletesWholeTrackAndRetainsMarkers_UndoRestoresOwner()
    {
        var(p,t)=Fixture();var w=new Waypoint{AssociatedTrackId=t.Id,Latitude=23,Longitude=113};p.Waypoints.Add(w);
        var plan=TrackGeometryBatchPlan.Prepare(p,[new(t.Id,t.GetSplitSections().Select(s=>s.Id).ToHashSet())],true);plan.Apply();Assert.Empty(p.Tracks);Assert.Single(p.Waypoints);Assert.Null(w.AssociatedTrackId);plan.Undo();Assert.Same(t,Assert.Single(p.Tracks));Assert.Equal(t.Id,w.AssociatedTrackId);plan.Apply();Assert.Empty(p.Tracks);
    }
    [Fact]
    public void BatchDelete_OnlyChosenSectionRemovesNoOtherGeometry()
    {
        var(p,t)=Fixture();var old=t.Segments.ToArray();var plan=TrackGeometryBatchPlan.Prepare(p,[new(t.Id,new HashSet<Guid>{t.GetSplitSections()[1].Id})],true);plan.Apply();Assert.Equal(new[]{old[0],old[2]},t.Segments);plan.Undo();Assert.Equal(old,t.Segments);
    }
    [Fact]
    public void BatchReverse_WholeAndSectionSelectionsHaveSingleUndo()
    {
        var(p,t)=Fixture();var(_,other)=Fixture();p.AddTrack(other,false);var a=Fingerprint(t);var b=Fingerprint(other);
        var plan=TrackGeometryBatchPlan.Prepare(p,[new(t.Id),new(other.Id,new HashSet<Guid>{other.GetSplitSections()[1].Id})],false);plan.Apply();Assert.NotEqual(a,Fingerprint(t));Assert.NotEqual(b,Fingerprint(other));plan.Undo();Assert.Equal(a,Fingerprint(t));Assert.Equal(b,Fingerprint(other));
    }
    [Fact]
    public void BatchApply_StaleLaterTrackRollsBackEarlierTrack()
    {
        var(p,t)=Fixture();var(_,other)=Fixture();p.AddTrack(other,false);var before=Fingerprint(t);
        var plan=TrackGeometryBatchPlan.Prepare(p,[new(t.Id),new(other.Id)],false);
        new TrackEditService().PrepareMovePoint(p,other.Id,other.Segments[0].Points[1].Id,new(23.001,113.001)).Apply();
        Assert.Throws<InvalidOperationException>(()=>plan.Apply());Assert.Equal(before,Fingerprint(t));Assert.Equal(0,t.GeometryRevision);
    }
    [Fact]
    public void RelatedMarkers_UsesDeletedSectionOnlyAndExcludesOtherTracks()
    {
        var(p,t)=Fixture();var inside=new Waypoint{AssociatedTrackId=t.Id,Latitude=23,Longitude=113.012};var outside=new Waypoint{AssociatedTrackId=t.Id,Latitude=23,Longitude=113.002};
        p.Waypoints.Add(inside);p.Waypoints.Add(outside);p.Waypoints.Add(new(){Latitude=23,Longitude=113.012});
        var plan=TrackGeometryBatchPlan.Prepare(p,[new(t.Id,new HashSet<Guid>{t.GetSplitSections()[1].Id})],true);
        Assert.Equal(new[]{inside.Id},plan.RelatedMarkerIds);
    }
    [Fact]
    public void RelatedMarkers_CutKeepsBothBoundaryMarkers()
    {
        var(p,t)=Fixture(1);var markers=new[]{1,2,4}.Select(i=>new Waypoint{AssociatedTrackId=t.Id,Latitude=23,Longitude=113+i*.001}).ToArray();foreach(var w in markers)p.Waypoints.Add(w);
        var plan=new TrackEditService().PrepareCutAndJoin(p,new(t.Id,Position(t,0,1),Position(t,0,4)));
        Assert.Equal(new[]{markers[1].Id},TrackRelatedMarkers.Find(p,t,plan.ChangeSet));
    }
    [Fact]
    public void RelatedMarkerDeletion_UndoRestoresPhotosLinksAndSharedBlob()
    {
        var(p,t)=Fixture();var a=new Waypoint{AssociatedTrackId=t.Id};var b=new Waypoint{AssociatedTrackId=t.Id};p.Waypoints.Add(a);p.Waypoints.Add(b);
        var blob=new MediaBlob{PendingBytes=[1,2,3]};p.MediaBlobs.Add(blob);
        var first=new MediaAttachment{OwnerId=a.Id,OwnerType=MediaOwnerType.Waypoint,MediaBlobId=blob.Id,Caption="原图片说明"};var second=new MediaAttachment{OwnerId=b.Id,OwnerType=MediaOwnerType.Waypoint,MediaBlobId=blob.Id};p.MediaAttachments.Add(first);p.MediaAttachments.Add(second);
        var link=new ExternalMediaReference{OwnerId=a.Id,OwnerType=MediaOwnerType.Waypoint,Url="https://example.invalid/photo.jpg",Caption="网络说明"};p.ExternalMediaReferences.Add(link);
        var deletion=new RelatedMarkerDeletion(p,new HashSet<Guid>{a.Id});deletion.Apply();Assert.Single(p.Waypoints);Assert.Single(p.MediaAttachments);Assert.Single(p.MediaBlobs);Assert.Empty(p.ExternalMediaReferences);
        deletion.Undo();Assert.Equal(2,p.Waypoints.Count);Assert.Contains(first,p.MediaAttachments);Assert.Contains(link,p.ExternalMediaReferences);Assert.Equal("原图片说明",first.Caption);Assert.Equal(new byte[]{1,2,3},blob.PendingBytes);
    }
    [Fact]
    public void MainGuideCut_RetainsBoundaryNodesAndArchivesManualContent()
    {
        var(p,t)=Fixture(1);p.MainGuideTrackId=t.Id;var service=new GuideStructureService();
        foreach(var i in new[]{1,2,4}){var n=new GuideNode{Name=$"节点{i}",TrackId=t.Id,OriginalCoordinate=t.Segments[0].Points[i].Coordinate};service.SnapNode(n,t);p.GuideNodes.Add(n);}
        GuideStructureService.ReplaceSegments(p,service.RebuildSegments(p));var manual=p.GuideSegments[0];manual.ManualData=new(){RoadCondition="人工路况保留"};
        var plan=new TrackEditService().PrepareCutAndJoin(p,new(t.Id,Position(t,0,1),Position(t,0,4)));plan.Apply();
        Assert.False(p.GuideNodes[0].IsOrphaned,"起始边界节点不应失联");
        Assert.True(p.GuideNodes[1].IsOrphaned);
        Assert.False(p.GuideNodes[2].IsOrphaned,"结束边界节点不应失联");
        Assert.Contains(p.GuideSegments.Concat(p.RetiredGuideSegments),s=>s.ManualData?.RoadCondition=="人工路况保留");
        plan.Undo();Assert.All(p.GuideNodes,n=>Assert.False(n.IsOrphaned));Assert.Contains(p.GuideSegments,s=>s.Id==manual.Id&&s.ManualData?.RoadCondition=="人工路况保留");
    }
    [Fact]
    public void TrimMarkerScope_IncludesDeletedOuterEndpointButNotPreviousSectionEndpoint()
    {
        var(p,t)=Fixture();var previous=new Waypoint{AssociatedTrackId=t.Id,Latitude=23,Longitude=t.Segments[0].Points[^1].Longitude};
        var removed=new Waypoint{AssociatedTrackId=t.Id,Latitude=23,Longitude=t.Segments[1].Points[0].Longitude};
        var kept=new Waypoint{AssociatedTrackId=t.Id,Latitude=23,Longitude=t.Segments[1].Points[2].Longitude};p.Waypoints.Add(previous);p.Waypoints.Add(removed);p.Waypoints.Add(kept);
        var plan=new TrackEditService().PrepareTrimAtPoint(p,Position(t,1,2),true,t.GetSplitSections()[1].Id);
        Assert.Equal(new[]{removed.Id},TrackRelatedMarkers.Find(p,t,plan.ChangeSet));
    }
    [Theory]
    [InlineData("TrackEditPointClicked")] [InlineData("DeleteSelectionRequested")]
    public void PointSelectionAndKeyboardMessages_AreAccepted(string type)
    { Assert.NotNull(MapProtocol.TryParseEvent($$"""{"protocolVersion":1,"type":"{{type}}"}""")); }
}
