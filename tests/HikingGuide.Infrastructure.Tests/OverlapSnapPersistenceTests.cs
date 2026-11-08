using HikingGuide.Core.Models;
using HikingGuide.Application.Guides;
using HikingGuide.Infrastructure.Projects;

namespace HikingGuide.Infrastructure.Tests;

public sealed class OverlapSnapPersistenceTests
{
    [Fact]
    public async Task ReturnBranchSurvivesSaveConversionResnapAndUndoWithoutMutatingOriginalTrack()
    {
        var track=new Track {Segments=[new() {Points=[new() {Latitude=23,Longitude=113},new() {Latitude=23,Longitude=113.01},new() {Latitude=23.01,Longitude=113.01},new() {Latitude=23,Longitude=113.01},new() {Latitude=23,Longitude=113}]}]}; track.RecalculateDistances();
        var candidates=new OverlapSnapService().Find(track,new(23,113.005)); Assert.Equal(2,candidates.Count);
        var chosen=candidates[^1]; Assert.True(chosen.DistanceFromTrackStart>track.GetTotalLength()/2);
        var project=new HikingProject(); project.AddTrack(track);
        var point=new Waypoint {Latitude=chosen.SnappedCoordinate.Latitude,Longitude=chosen.SnappedCoordinate.Longitude,AssociatedTrackId=track.Id,PreferredTrackPosition=chosen}; project.Waypoints.Add(point);
        var root=Path.Combine(Path.GetTempPath(),"HikingGuide-Overlap-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var file=Path.Combine(root,"test.hikeproj"); var store=new ProjectFileService(); await store.SaveAsync(project,file,false);
            var reopened=await store.LoadAsync(file); var restored=Assert.Single(reopened.Waypoints); Assert.Equal(chosen,restored.PreferredTrackPosition);
            var conversion=new BatchWaypointGuideConversion().Prepare(reopened,[restored]); conversion.Apply(); var node=Assert.Single(reopened.GuideNodes);
            Assert.Equal(chosen.DistanceFromTrackStart,node.ExactPosition!.DistanceFromTrackStart);
            new GuideStructureService().SnapNode(node,reopened.Tracks[0]); Assert.Equal(chosen.DistanceFromTrackStart,node.ExactPosition.DistanceFromTrackStart);
            Assert.Equal(chosen,node.ToWaypoint().PreferredTrackPosition); conversion.Undo(); Assert.Same(restored,Assert.Single(reopened.Waypoints));
            Assert.Equal(5,reopened.Tracks[0].GetPointCount());
        }
        finally {Directory.Delete(root,true);}
    }
    [Fact]
    public void GeometryChangedChoiceIsNotReused_AndInvalidOptionsReject()
    {
        var track=new Track {Segments=[new() {Points=[new() {Latitude=23,Longitude=113},new() {Latitude=23,Longitude=113.01}]}]};
        var old=new OverlapSnapService().Find(track,new(23,113.005))[0];
        track.Segments[0].Points[1].Latitude=23.01;
        var current=new GuideStructureService().SnapNodeToPosition(track,new(23,113.005),old);
        Assert.NotEqual(old.TrackGeometryFingerprint,current.TrackGeometryFingerprint);
        Assert.Throws<InvalidOperationException>(()=>new OverlapSnapService().Find(track,new(23,113),new(ExtraDistanceMeters:-1)));
        Assert.Throws<OperationCanceledException>(()=>new OverlapSnapService().Find(track,new(23,113),token:new CancellationToken(true)));
    }
}
