using HikingGuide.Application.Media;
using HikingGuide.Core.Models;
namespace HikingGuide.Application.Tests;
public sealed class MarkerPhotoNavigationTests
{
    [Fact]public void SameTrackOrderedSkipsNoPhotoAndOtherTracksWithoutMutation()
    {
        var track=new Track{Segments=[new(){Points=[new(){Latitude=24,Longitude=113},new(){Latitude=24,Longitude=113.01}]}]};track.RecalculateDistances();
        var p=new HikingProject();p.AddTrack(track,false);var first=new Waypoint{Name="前",Latitude=24,Longitude=113.001,AssociatedTrackId=track.Id};var empty=new Waypoint{Latitude=24,Longitude=113.002,AssociatedTrackId=track.Id};var last=new Waypoint{Name="后",Latitude=24,Longitude=113.008,AssociatedTrackId=track.Id};var other=new Waypoint{Latitude=24,Longitude=113.004,AssociatedTrackId=Guid.NewGuid()};var guide=new GuideNode{Name="攻略点",TrackId=track.Id,OriginalCoordinate=new(24,113.005)};
        p.Waypoints.Add(last);p.Waypoints.Add(other);p.Waypoints.Add(empty);p.Waypoints.Add(first);p.GuideNodes.Add(guide);
        foreach(var id in new[]{first.Id,last.Id,other.Id})p.MediaAttachments.Add(new(){OwnerId=id});p.ExternalMediaReferences.Add(new(){OwnerId=guide.Id,Url="https://example.com/photo.png"});p.MarkSaved();
        var result=new MarkerPhotoNavigation().Build(p,last);
        Assert.Equal(new[]{first.Id,guide.Id,last.Id},result.Select(m=>m.Id));Assert.Null(first.PreferredTrackPosition);Assert.Null(guide.ExactPosition);Assert.False(p.IsDirty);Assert.Equal(track.Id,first.AssociatedTrackId);
    }
    [Fact]public void UnassociatedPointsDoNotBrowseOtherUnassociatedPoints()
    {
        var p=new HikingProject();var a=new Waypoint();var b=new Waypoint();p.Waypoints.Add(a);p.Waypoints.Add(b);p.ExternalMediaReferences.Add(new(){OwnerId=a.Id});p.ExternalMediaReferences.Add(new(){OwnerId=b.Id});Assert.Equal(new[]{a.Id},new MarkerPhotoNavigation().Build(p,a).Select(m=>m.Id));
    }
}
