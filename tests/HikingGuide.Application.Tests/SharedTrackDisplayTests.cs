using System.Text.Json;
using HikingGuide.Application.Guides;
using HikingGuide.Application.Maps;
using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
namespace HikingGuide.Application.Tests;
public sealed class SharedTrackDisplayTests
{
    static (HikingProject Project,Track Track) Make(int count=1600)
    {
        var track=new Track{Name="精度测试",Segments=[new(){Points=Enumerable.Range(0,count).Select(i=>new TrackPoint{Longitude=113+i*.00001,Latitude=23+Math.Sin(i*.03)*.000001}).ToList()}]};
        track.RecalculateDistances();var p=new HikingProject();p.AddTrack(track);return(p,track);
    }
    [Theory][InlineData(15)][InlineData(15.1)][InlineData(22)]
    public void AtAndAbove15PreservesEveryPointAndBreak(double zoom)
    {
        var(p,t)=Make();t.Segments.Add(new(){Points=[new(){Longitude=114,Latitude=23},new(){Longitude=114.1,Latitude=23}]});t.NotifyGeometryChanged();
        var f=new MapDisplayDataService(new DouglasPeuckerTrackDisplaySimplifier()).Create(p,zoom,1).Tracks.Single();
        Assert.Equal(t.GetPointCount(),f.DisplayPointCount);
        for(int i=0;i<t.Segments.Count;i++)Assert.Equal(t.Segments[i].Points.Select(v=>new MapCoordinate(v.Longitude,v.Latitude)),f.Segments[i]);
        Assert.All(f.RenderPieces!,piece=>Assert.InRange(piece.Coordinates.Count,2,512));
    }
    [Fact]public void ThresholdAndGentlerLowZoomAreExplicit()
    {var p=MapDisplayProfile.Default;Assert.Equal(0,p.GetToleranceMeters(15));Assert.Equal(.75,p.GetToleranceMeters(14.99));Assert.InRange(p.GetToleranceMeters(14),.1,2);Assert.NotEqual(p.GetCacheZoomBucket(14.99),p.GetCacheZoomBucket(15));Assert.Equal(p.GetCacheZoomBucket(15),p.GetCacheZoomBucket(22));}
    [Fact]public void GuideCutPointsAreSharedAndOriginalUntouched()
    {
        var(p,t)=Make();var before=JsonSerializer.Serialize(t);var gs=new GuideStructureService();
        foreach(var x in new[]{113.002345,113.008765}){var n=new GuideNode{TrackId=t.Id,OriginalCoordinate=new GeoCoordinate(23,x)};gs.SnapNode(n,t);p.GuideNodes.Add(n);}
        foreach(var g in gs.RebuildSegments(p))p.GuideSegments.Add(g);
        var data=new MapDisplayDataService(new DouglasPeuckerTrackDisplaySimplifier()).Create(p,14,1);var f=data.Tracks.Single();
        var guide=Assert.Single(p.GuideSegments);var parts=f.RenderPieces!.Where(v=>v.GuideSegmentId==guide.Id).ToArray();Assert.NotEmpty(parts);
        Assert.Equal(new MapCoordinate(p.GuideNodes[0].ExactPosition!.SnappedCoordinate.Longitude,p.GuideNodes[0].ExactPosition!.SnappedCoordinate.Latitude),parts[0].Coordinates[0]);
        Assert.All(parts,v=>Assert.All(v.Coordinates,c=>Assert.Contains(c,f.Segments.SelectMany(x=>x))));
        Assert.Equal(before,JsonSerializer.Serialize(t));
        using var visible=JsonDocument.Parse(new MapGeoJsonBuilder().BuildTracks(data,p));Assert.Contains(visible.RootElement.GetProperty("features").EnumerateArray(),v=>v.GetProperty("properties").GetProperty("guideVisible").GetBoolean());
        MapLayerVisibility.SetGuideSegmentVisible(p.Settings.Map,guide.Id,false);
        using var hidden=JsonDocument.Parse(new MapGeoJsonBuilder().BuildTracks(data,p));Assert.All(hidden.RootElement.GetProperty("features").EnumerateArray(),v=>Assert.False(v.GetProperty("properties").GetProperty("guideVisible").GetBoolean()));
    }
    [Fact]public void ChunkIdsAreUniqueAndAdjacentChunksMeetExactly()
    {
        var(p,t)=Make();var data=new MapDisplayDataService(new DouglasPeuckerTrackDisplaySimplifier()).Create(p,15,1);var f=data.Tracks.Single();
        var pieces=f.RenderPieces!;for(int i=1;i<pieces.Count;i++)Assert.Equal(pieces[i-1].Coordinates[^1],pieces[i].Coordinates[0]);
        using var json=JsonDocument.Parse(new MapGeoJsonBuilder().BuildTracks(data,p));var ids=json.RootElement.GetProperty("features").EnumerateArray().Select(v=>v.GetProperty("properties").GetProperty("chunkId").GetString()).ToArray();Assert.Equal(ids.Length,ids.Distinct().Count());
    }
    [Fact]public void GeometryRevisionInvalidatesSharedSnapshot()
    {var(p,t)=Make();var service=new MapDisplayDataService(new DouglasPeuckerTrackDisplaySimplifier());var first=service.Create(p,15,1);t.Segments[0].Points[10].Longitude+=.01;t.NotifyGeometryChanged();var second=service.Create(p,15,2);Assert.NotEqual(first.Tracks[0].Segments[0][10],second.Tracks[0].Segments[0][10]);}
}
