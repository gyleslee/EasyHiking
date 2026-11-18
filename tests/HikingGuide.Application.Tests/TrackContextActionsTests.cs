using System.Text.Json;
using HikingGuide.Application.Maps;
using HikingGuide.Application.TrackEditing;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;
using Xunit;
namespace HikingGuide.Application.Tests;
public sealed class TrackContextActionsTests
{
    private static (HikingProject Project,Track Track) Fixture()
    {
        var p=new HikingProject();var t=new Track{Name="右键测试",Segments=Enumerable.Range(0,2).Select(s=>new TrackSegment
        {SplitSectionId=Guid.NewGuid(),Points=Enumerable.Range(0,6).Select(i=>new TrackPoint{Latitude=23+s*.00001,Longitude=113+i*.001}).ToList()}).ToList()};
        t.RecalculateDistances();p.AddTrack(t,false);return(p,t);
    }
    [Fact]
    public void RightClickBoundary_UsesExactLinePositionAndWorksAsFirstCutBoundary()
    {
        var(p,t)=Fixture();var resolver=new TrackContextPositionResolver();var first=resolver.Resolve(t,new(23,113.0015),t.GetSplitSections()[0].Id);
        var last=resolver.Resolve(t,new(23,113.004),t.GetSplitSections()[0].Id,true);
        Assert.Equal(.5,first.Fraction,6);var plan=new TrackEditService().PrepareCutAndJoin(p,new(t.Id,first,last),t.GetSplitSections()[0].Id);plan.Apply();
        Assert.Contains(t.Segments[0].Points,pt=>Math.Abs(pt.Longitude-113.0015)<1e-9);Assert.Equal(5,t.Segments[0].Points.Count);Assert.Equal(6,t.Segments[1].Points.Count);
    }
    [Fact]
    public void GPSPlacement_RespectsClickedOverlappingSectionAndFullFingerprint()
    {
        var(p,t)=Fixture();var resolver=new TrackContextPositionResolver();var fingerprint=new TrackSnapService().GetGeometryFingerprint(t);
        var position=resolver.Resolve(t,new(23,113.0021),t.GetSplitSections()[1].Id,true);
        Assert.Equal(t.Segments[1].Id,position.TrackSegmentId);Assert.Equal(1,position.SegmentIndex);Assert.Equal(t.Segments[1].Points[2].Coordinate,position.SnappedCoordinate);
        Assert.Equal(fingerprint,position.TrackGeometryFingerprint);Assert.True(position.DistanceFromTrackStart>t.Segments[0].GetLength());
    }
    [Fact]
    public void MissingContextSection_RejectsInsteadOfSnappingToOtherTrackPart()
    {var(_,t)=Fixture();Assert.Throws<InvalidOperationException>(()=>new TrackContextPositionResolver().Resolve(t,new(23,113),Guid.NewGuid(),true));}
    [Fact]
    public void HiddenSection_FiltersSharedGeometryAndPreservesCacheAndAllRawPoints()
    {
        var(p,t)=Fixture();var service=new MapDisplayDataService(new DouglasPeuckerTrackDisplaySimplifier());var before=service.Create(p,16,1);var section=t.GetSplitSections()[0].Id;var fingerprint=new TrackSnapService().GetGeometryFingerprint(t);
        MapLayerVisibility.SetSplitSectionVisible(p.Settings.Map,section,false);var hidden=service.Create(p,16,2);var track=Assert.Single(hidden.Tracks);
        Assert.Empty(track.Segments[0]);Assert.Equal(2,track.Segments.Count);Assert.DoesNotContain(track.RenderPieces!,x=>x.SplitSectionId==section);
        Assert.Equal(12,t.GetPointCount());Assert.Equal(fingerprint,new TrackSnapService().GetGeometryFingerprint(t));Assert.True(MapLayerVisibility.IsTrackVisible(p.Settings.Map,t.Id));
        using var json=JsonDocument.Parse(new MapGeoJsonBuilder().BuildTracks(hidden,p));Assert.All(json.RootElement.GetProperty("features").EnumerateArray(),f=>Assert.NotEqual(section,f.GetProperty("properties").GetProperty("splitSectionId").GetGuid()));
        MapLayerVisibility.SetSplitSectionVisible(p.Settings.Map,section,true);var restored=service.Create(p,16,3);Assert.Equal(before.Tracks[0].RenderPieces!.Count,restored.Tracks[0].RenderPieces!.Count);
    }
    [Fact]
    public void SplitOverlayBuilder_SkipsHiddenSectionWithoutBreakingPartIndices()
    {
        var(p,t)=Fixture();MapLayerVisibility.SetSplitSectionVisible(p.Settings.Map,t.GetSplitSections()[0].Id,false);
        var data=new MapDisplayDataService(new DouglasPeuckerTrackDisplaySimplifier()).Create(p,16,1);
        using var json=JsonDocument.Parse(new SplitSectionMapGeoJsonBuilder().Build(p,data));Assert.Equal(1,json.RootElement.GetProperty("features").GetArrayLength());
    }
}
