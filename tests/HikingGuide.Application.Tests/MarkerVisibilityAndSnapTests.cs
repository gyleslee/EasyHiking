using HikingGuide.Application.Guides;
using HikingGuide.Application.Maps;
using HikingGuide.Core.Models;
using HikingGuide.Core.Geometry;

namespace HikingGuide.Application.Tests;

public sealed class MarkerVisibilityAndSnapTests
{
    private static (HikingProject P, Track A, Track B) Fixture()
    {
        Track Make(double lat)=>new() { Segments=[new() {Points=[new() {Latitude=lat,Longitude=113},new() {Latitude=lat,Longitude=113.01}]}]};
        var a=Make(23); var b=Make(23.001); a.RecalculateDistances(); b.RecalculateDistances();
        var p=new HikingProject(); p.AddTrack(a); p.AddTrack(b,false); return(p,a,b);
    }
    [Fact]
    public void VisibleOwnerHasPriorityEvenWhenAnotherTrackIsCloser()
    {
        var (p,a,b)=Fixture(); var result=new MarkerTrackSnapService().FindNearest(p,new(23.001,113.005),false,preferredTrackId:a.Id);
        Assert.Equal(a.Id,result.Track.Id); Assert.True(result.Position.DistanceToTrack>50);
        MapLayerVisibility.SetTrackVisible(p.Settings.Map,a.Id,false);
        Assert.Equal(b.Id,new MarkerTrackSnapService().FindNearest(p,new(23,113.005),false,preferredTrackId:a.Id).Track.Id);
    }
    [Fact]
    public void HiddenTracksAreExcludedAndNoVisibleOrHiddenMainRejects()
    {
        var(p,a,b)=Fixture(); MapLayerVisibility.SetTrackVisible(p.Settings.Map,a.Id,false);
        Assert.Equal(b.Id,new MarkerTrackSnapService().FindNearest(p,new(23,113.005),false).Track.Id);
        Assert.Throws<InvalidOperationException>(()=>new MarkerTrackSnapService().FindNearest(p,new(23,113.005),true));
        MapLayerVisibility.SetTrackVisible(p.Settings.Map,b.Id,false);
        Assert.Throws<InvalidOperationException>(()=>new MarkerTrackSnapService().FindNearest(p,new(23,113.005),false));
    }
    [Fact]
    public void HideParentHidesOwnedNodesAndGuideSegmentsButKeepsDataAndRestoresOnShow()
    {
        var(p,a,b)=Fixture(); var w=new Waypoint { AssociatedTrackId=a.Id,Latitude=23,Longitude=113.005 }; p.Waypoints.Add(w);
        var n=new GuideNode { TrackId=a.Id,OriginalCoordinate=new(23,113.001) }; var end=new GuideNode {TrackId=a.Id,OriginalCoordinate=new(23,113.009)};
        var service=new GuideStructureService(); service.SnapNode(n,a); service.SnapNode(end,a); p.GuideNodes.Add(n); p.GuideNodes.Add(end);
        GuideStructureService.ReplaceSegments(p,service.RebuildSegments(p));
        var unassociated=new Waypoint(); p.Waypoints.Add(unassociated);
        // 地图显示简化实现与原始点分离，隐藏只改变输出。
        var display=new MapDisplayDataService(new DouglasPeuckerTrackDisplaySimplifier());
        MapLayerVisibility.SetTrackVisible(p.Settings.Map,a.Id,false);
        Assert.Single(display.Create(p,12,1).Waypoints);
        var hidden=new GuideMapGeoJsonBuilder().Build(p); Assert.Empty(hidden.Nodes.GetProperty("features").EnumerateArray()); Assert.Empty(hidden.Segments.GetProperty("features").EnumerateArray());
        Assert.Equal(2,p.GuideNodes.Count); Assert.Equal(2,p.Waypoints.Count); Assert.Single(p.GuideSegments);
        MapLayerVisibility.SetTrackVisible(p.Settings.Map,a.Id,true);
        Assert.Equal(2,display.Create(p,12,2).Waypoints.Count); Assert.Equal(2,new GuideMapGeoJsonBuilder().Build(p).Nodes.GetProperty("features").GetArrayLength());
    }
}
