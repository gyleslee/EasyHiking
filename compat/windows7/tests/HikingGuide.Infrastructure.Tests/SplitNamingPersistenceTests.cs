using HikingGuide.Application.TrackEditing;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Projects;

namespace HikingGuide.Infrastructure.Tests;
public sealed class SplitNamingPersistenceTests
{
    private sealed class TemporaryDirectory:IDisposable
    {
        public string Path {get;}=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"HikingGuide.SplitNaming",Guid.NewGuid().ToString("N"));
        public TemporaryDirectory()=>Directory.CreateDirectory(Path);
        public void Dispose(){if(Directory.Exists(Path))Directory.Delete(Path,true);}
    }
    [Fact]public async Task DeletedHighestNumberStaysReservedAfterSavingAndReopening()
    {
        using var directory=new TemporaryDirectory();var path=Path.Combine(directory.Path,"编号.hikeproj");
        var track=new Track{Name="1",Segments=[new(){Points=Enumerable.Range(0,9).Select(i=>new TrackPoint{Latitude=23,Longitude=113+i*.001}).ToList()}]};
        track.RecalculateDistances();var project=new HikingProject();project.AddTrack(track,false);project.MainGuideTrackId=null;
        var editor=new TrackEditService();editor.PrepareSplitSegment(project,track.Id,track.Segments[0].Id,2,.5).Apply();
        editor.PrepareSplitSegment(project,track.Id,track.Segments[^1].Id,0,.5).Apply();
        editor.PrepareDeleteSplitSection(project,track.Id,track.GetSplitSections()[2].Id).Apply();
        var file=new ProjectFileService();await file.SaveAsync(project,path,false);var reopened=await file.LoadAsync(path);var restored=reopened.Tracks[0];
        Assert.Equal("3",restored.Metadata[TrackSplitNaming.LastNumberKey]);
        editor.PrepareSplitSegment(reopened,restored.Id,restored.Segments[0].Id,0,.5).Apply();
        Assert.Contains(restored.GetSplitSections(),s=>s.Name=="1（4）");Assert.DoesNotContain(restored.GetSplitSections(),s=>s.Name=="1（3）");
        Assert.True(File.Exists(path));
    }
}
