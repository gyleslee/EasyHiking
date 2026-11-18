using HikingGuide.Application.TrackEditing;
using HikingGuide.Application.Maps;
using HikingGuide.Core.Models;
using HikingGuide.Core.Services;
using Xunit;
namespace HikingGuide.Application.Tests;

public sealed class IndependentTracksTests
{
    private static (HikingProject P,Track T) Make()
    {
        var p=new HikingProject();var t=new Track{Name="山路",DisplayColor="#ABC123",Segments=[new(){Points=Enumerable.Range(0,10)
            .Select(i=>new TrackPoint{Latitude=23,Longitude=113+i*.001,SourceElevation=100+i,Timestamp=DateTimeOffset.UnixEpoch.AddSeconds(i)}).ToList()}]};
        p.AddTrack(t,false);t.RecalculateDistances();return(p,t);
    }
    private static (TrackEditPlan Edit,IndependentTracks Conversion) Split(HikingProject p,Track t,int index=3)
    {
        var plan=new TrackEditService().PrepareSplitSegment(p,t.Id,t.Segments[0].Id,index,1);plan.Apply();
        var convert=new IndependentTracks(p,t);convert.Apply();return(plan,convert);
    }
    [Fact] public void SplitProducesIndependentNamedTracks_AndTransfersMarkerWithoutLosingSourcePoints()
    {
        var(p,t)=Make();var ids=t.Segments[0].Points.Select(x=>x.Id).ToArray();var marker=new Waypoint{AssociatedTrackId=t.Id,Latitude=23,Longitude=113.008};
        marker.PreferredTrackPosition=new TrackSnapService().Snap(t,marker.Coordinate).Position;p.Waypoints.Add(marker);
        var(edit,c)=Split(p,t);
        Assert.Equal(new[]{"山路（1）","山路（2）"},p.Tracks.Select(x=>x.Name));
        Assert.All(p.Tracks,x=>Assert.Single(x.GetSplitSections()));Assert.Equal(p.Tracks[1].Id,marker.AssociatedTrackId);
        Assert.All(ids,id=>Assert.Contains(p.Tracks.SelectMany(x=>x.Segments).SelectMany(x=>x.Points),pt=>pt.Id==id));
        Assert.Equal("#ABC123",t.DisplayColor);Assert.NotEqual(t.DisplayColor,p.Tracks[1].DisplayColor);
        c.Undo();edit.Undo();Assert.Single(p.Tracks);Assert.Equal("山路",t.Name);Assert.Equal(t.Id,marker.AssociatedTrackId);
        Assert.Equal(ids,t.Segments[0].Points.Select(x=>x.Id));
        edit.Apply();c.Apply();Assert.Equal(2,p.Tracks.Count);Assert.Equal("山路（1）",t.Name);
    }
    [Fact] public void DeletingHighestNumberDoesNotReuseItOnNextSplit()
    {
        var(p,t)=Make();Split(p,t);p.RemoveTrack(p.Tracks[1].Id);Split(p,t,1);
        Assert.Equal(new[]{"山路（1）","山路（3）"},p.Tracks.Select(x=>x.Name));
    }
    [Fact] public void LegacyNumberingGapsAndPhysicalPartsArePreserved()
    {
        var(p,t)=Make();var second=new TrackSegment{SplitSectionName="山路（4）",Points=t.Segments[0].Points.Skip(6).ToList()};
        t.Segments[0].Points=t.Segments[0].Points.Take(6).ToList();t.Segments[0].SplitSectionName="山路（1）";t.Segments.Add(second);
        var original=t.GetPointCount();new IndependentTracks(p,t).Apply();Assert.Equal(new[]{"山路（1）","山路（4）"},p.Tracks.Select(x=>x.Name));
        Assert.Equal(original,p.Tracks.Sum(x=>x.GetPointCount()));Assert.Equal("4",t.Metadata[IndependentTracks.LastKey]);
    }
    [Fact] public void SingleLogicalRouteWithPhysicalGapsIsNotSplit()
    {
        var(p,t)=Make();var section=Guid.NewGuid();t.Segments[0].SplitSectionId=section;t.Segments.Add(new(){SplitSectionId=section,Points=[new(){Latitude=24,Longitude=114},new(){Latitude=24.1,Longitude=114.1}]});
        new IndependentTracks(p,t).Apply();Assert.Single(p.Tracks);Assert.Equal(2,t.Segments.Count);
    }
    [Fact] public void ContinuationPreservesOriginalCoordinatesAndGuides_AndIsUndoable()
    {
        var(p,t)=Make();p.MainGuideTrackId=t.Id;var points=t.Segments[0].Points.ToArray();
        var node=new GuideNode{TrackId=t.Id,OriginalCoordinate=points[2].Coordinate,SnappedCoordinate=points[2].Coordinate,
            ExactPosition=new TrackSnapService().Snap(t,points[2].Coordinate).Position,Name="原攻略节点"};p.GuideNodes.Add(node);
        var ext=new Track{Segments=[new(){Points=[new(){Latitude=23,Longitude=113.0092},new(){Latitude=23,Longitude=113.01}]}]};
        var plan=new TrackEditService().PrepareAppend(p,t,ext);Assert.Contains(plan.Warnings,x=>x.Contains("直线接入"));plan.Apply();
        Assert.Equal(points.Select(x=>x.Coordinate),t.Segments[0].Points.Take(10).Select(x=>x.Coordinate));Assert.False(node.IsOrphaned);
        Assert.Equal("山路",t.Name);Assert.Equal("#ABC123",t.DisplayColor);Assert.Equal(12,t.GetPointCount());
        plan.Undo();Assert.Equal(10,t.GetPointCount());Assert.Contains(node,p.GuideNodes);
    }
    [Fact] public void NewPlannedColorsDoNotCollideWithProjectColors()
    {var(p,_)=Make();for(int i=0;i<100;i++)p.AddTrack(new(){DisplayColor=TrackDisplayColor.NextUnique(p.Tracks)},false);Assert.Equal(p.Tracks.Count,p.Tracks.Select(t=>t.DisplayColor).Distinct().Count());}
    [Fact] public void LegacyHiddenSectionStaysHiddenAfterConversionAndUndoRestoresSettings()
    {
        var(p,t)=Make();var section=new TrackSegment{Points=[new(){Latitude=24,Longitude=114},new(){Latitude=24.1,Longitude=114.1}]};t.Segments.Add(section);
        MapLayerVisibility.SetSplitSectionVisible(p.Settings.Map,section.Id,false);
        var before=p.Settings.Map.LayerVisibility.ToArray();var convert=new IndependentTracks(p,t);convert.Apply();
        Assert.True(MapLayerVisibility.IsTrackVisible(p.Settings.Map,t.Id));Assert.False(MapLayerVisibility.IsTrackVisible(p.Settings.Map,p.Tracks[1].Id));
        convert.Undo();Assert.Equal(before,p.Settings.Map.LayerVisibility.ToArray());
    }
}
