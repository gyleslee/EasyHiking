using HikingGuide.Application.Guides;
using HikingGuide.Core.Models;

namespace HikingGuide.Application.Tests;

public sealed class BatchMarkerEditTests
{
    private static (HikingProject P, Track Main, Track Other, Waypoint W, GuideNode N) Fixture()
    {
        var main = new Track { Segments = [new() { Points = [new() { Latitude=23, Longitude=113 }, new() { Latitude=23, Longitude=113.01 }] }] };
        var other = new Track { Name="普通轨迹" }; main.RecalculateDistances();
        var p = new HikingProject(); p.AddTrack(main); p.AddTrack(other,false);
        var w = new Waypoint { Name="普通点", Latitude=23, Longitude=113.005, AssociatedTrackId=other.Id, ManualData=new() { Notes="人工备注" } };
        var n = w.ToGuideNode(); n.Id=Guid.NewGuid(); n.Name="攻略点"; n.TrackId=main.Id;
        new GuideStructureService().SnapNode(n,main); p.Waypoints.Add(w); p.GuideNodes.Add(n);
        return (p,main,other,w,n);
    }
    [Fact]
    public void PromoteMixedSelection_UsesMainPreservesMediaFieldsAndUndoRedo()
    {
        var (p,main,other,w,n)=Fixture();
        var image=new MediaAttachment { OwnerId=w.Id, OwnerType=MediaOwnerType.Waypoint }; p.MediaAttachments.Add(image);
        var plan=new BatchMarkerEdit().Prepare(p,[w,n],new(ApplyName:true,Name:"共同名称",ApplyGuideState:true,IsGuideNode:true));
        Assert.Single(p.Waypoints);
        for(var i=0;i<3;i++)
        {
            plan.Apply(); Assert.Empty(p.Waypoints); Assert.Equal(2,p.GuideNodes.Count);
            Assert.All(p.GuideNodes,g=>{ Assert.Equal(main.Id,g.TrackId); Assert.Equal("共同名称",g.Name); });
            var promoted=p.GuideNodes.Single(g=>g.Id==w.Id); Assert.NotNull(promoted.ExactPosition);
            Assert.Equal("人工备注",promoted.ManualData!.Notes); Assert.Equal(MediaOwnerType.GuideNode,image.OwnerType);
            plan.Undo(); Assert.Same(w,Assert.Single(p.Waypoints)); Assert.Same(n,Assert.Single(p.GuideNodes));
            Assert.Equal(other.Id,w.AssociatedTrackId); Assert.Equal("攻略点",n.Name); Assert.Equal(MediaOwnerType.Waypoint,image.OwnerType);
        }
    }
    [Fact]
    public void DemoteMixedSelection_AssignsChosenOwnerAndRestoresOriginals()
    {
        var (p,_,other,w,n)=Fixture();
        var link=new ExternalMediaReference { OwnerId=n.Id,OwnerType=MediaOwnerType.GuideNode,Url="https://example.com/a.jpg" }; p.ExternalMediaReferences.Add(link);
        var plan=new BatchMarkerEdit().Prepare(p,[w,n],new(ApplyGuideState:true,IsGuideNode:false,ApplyTrack:true,TrackId:other.Id));
        plan.Apply(); Assert.Empty(p.GuideNodes); Assert.Equal(2,p.Waypoints.Count); Assert.All(p.Waypoints,x=>Assert.Equal(other.Id,x.AssociatedTrackId));
        Assert.Equal(MediaOwnerType.Waypoint,link.OwnerType); plan.Undo(); Assert.Same(n,Assert.Single(p.GuideNodes)); Assert.Same(w,Assert.Single(p.Waypoints));
        Assert.Equal(MediaOwnerType.GuideNode,link.OwnerType);
    }
    [Fact]
    public void OwnerOnly_AffectsOrdinaryMarkersNotGuideIdentityOrCoordinates()
    {
        var (p,main,_,w,n)=Fixture(); var coordinate=w.Coordinate; var oldOwner=w.AssociatedTrackId;
        var plan=new BatchMarkerEdit().Prepare(p,[w,n],new(ApplyTrack:true,TrackId:null));
        plan.Apply(); Assert.Null(w.AssociatedTrackId); Assert.Equal(main.Id,n.TrackId); Assert.Equal(coordinate,w.Coordinate);
        plan.Undo(); Assert.Equal(oldOwner,w.AssociatedTrackId);
    }
    [Fact]
    public void MissingMainOwnerOrCancellation_RejectWithoutMutation()
    {
        var (p,_,_,w,n)=Fixture(); var service=new BatchMarkerEdit(); p.MainGuideTrackId=null;
        Assert.Throws<InvalidOperationException>(()=>service.Prepare(p,[w,n],new(ApplyGuideState:true,IsGuideNode:true)));
        Assert.Throws<InvalidOperationException>(()=>service.Prepare(p,[w],new(ApplyTrack:true,TrackId:Guid.NewGuid())));
        Assert.Throws<OperationCanceledException>(()=>service.Prepare(p,[w],new(ApplyName:true),new CancellationToken(true)));
        Assert.Single(p.Waypoints); Assert.Single(p.GuideNodes);
    }
    [Fact]
    public void MixedSelection_OnlyCheckedFieldsChange_AndUndoRedoPreservesContent()
    {
        var p = new HikingProject();
        var w = new Waypoint { Name = "水源", Description = "原描述甲", AssociatedTrackId = Guid.NewGuid(), ManualData = new() { Notes = "人工内容" } };
        var n = new GuideNode { Name = "营地", Description = "原描述乙", TrackId = Guid.NewGuid() };
        p.Waypoints.Add(w); p.GuideNodes.Add(n);
        var attachment = new MediaAttachment { OwnerId = n.Id }; p.MediaAttachments.Add(attachment);
        var plan = new BatchMarkerEdit().Prepare(p, [w, n], new(ApplyDescription: true, Description: "共同描述"));
        for (var i = 0; i < 3; i++)
        {
            plan.Apply(); Assert.Equal("共同描述", w.Description); Assert.Equal("共同描述", n.Description);
            Assert.Equal("水源", w.Name); Assert.Equal("营地", n.Name);
            Assert.NotNull(w.AssociatedTrackId); Assert.NotNull(n.TrackId);
            Assert.Equal("人工内容", w.ManualData!.Notes); Assert.Same(attachment, Assert.Single(p.MediaAttachments));
            plan.Undo(); Assert.Equal("原描述甲", w.Description); Assert.Equal("原描述乙", n.Description);
        }
    }
    [Fact]
    public void ExplicitEmpty_ClearsOnlySelectedFields()
    {
        var p = new HikingProject(); var w = new Waypoint { Name = "名", Description = "描述", Type = WaypointType.Custom, CustomTypeName = "自定义甲" }; p.Waypoints.Add(w);
        var plan = new BatchMarkerEdit().Prepare(p, [w], new(ApplyName: true, Name: "", ApplyType: true, Type: WaypointType.Camp));
        plan.Apply(); Assert.Equal("", w.Name); Assert.Equal("描述", w.Description); Assert.Null(w.CustomTypeName);
        plan.Undo(); Assert.Equal("名", w.Name); Assert.Equal("自定义甲", w.CustomTypeName);
    }
    [Fact]
    public void InvalidSelectionOrPatch_DoesNotMutate()
    {
        var p = new HikingProject(); var w = new Waypoint { Name = "名" }; p.Waypoints.Add(w); var service = new BatchMarkerEdit();
        Assert.Throws<InvalidOperationException>(() => service.Prepare(p, [w], new()));
        Assert.Throws<InvalidOperationException>(() => service.Prepare(p, [new Waypoint()], new(ApplyName: true)));
        Assert.Throws<InvalidOperationException>(() => service.Prepare(p, [w,w], new(ApplyName: true)));
        Assert.Throws<InvalidOperationException>(() => service.Prepare(p, [w], new(ApplyType: true, Type: WaypointType.Custom)));
        Assert.Equal("名", w.Name);
    }
}
