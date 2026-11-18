using HikingGuide.Core.Geometry;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Projects;

namespace HikingGuide.Infrastructure.Tests;

public sealed class ProjectMapSettingsTests
{
    [Fact]
    public async Task SplitVisibility_RoundTripsWithoutChangingTrackOrStatistics()
    {
        var folder=Path.Combine(Path.GetTempPath(),"HikingGuide.SplitVisibility",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        try
        {
            var track=new Track{Segments=Enumerable.Range(0,2).Select(i=>new TrackSegment{SplitSectionId=Guid.NewGuid(),Points=[new(){Latitude=23,Longitude=113+i*.01},new(){Latitude=23,Longitude=113.005+i*.01}]}).ToList()};track.RecalculateDistances();var project=new HikingProject();project.AddTrack(track,false);
            var id=track.GetSplitSections()[0].Id;MapLayerVisibility.SetSplitSectionVisible(project.Settings.Map,id,false);var length=track.GetTotalLength();
            var path=Path.Combine(folder,"显隐.hikeproj");var files=new ProjectFileService();await files.SaveAsync(project,path,false);var restored=await files.LoadAsync(path);
            Assert.False(MapLayerVisibility.IsSplitSectionVisible(restored.Settings.Map,id));Assert.True(MapLayerVisibility.IsTrackVisible(restored.Settings.Map,track.Id));Assert.Equal(length,restored.Tracks[0].GetTotalLength(),6);Assert.Equal(4,restored.Tracks[0].GetPointCount());
        }
        finally{Directory.Delete(folder,true);}
    }
    [Fact]
    public async Task ProjectFile_RoundTripsOnlySelectedMapSourceIdAndViewState()
    {
        var directory = Path.Combine(Path.GetTempPath(), "HikingGuide.P4.ProjectTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "地图状态.hikeproj");
            var sourceId = Guid.NewGuid();
            var project = new HikingProject { Name = "地图状态测试" };
            project.Settings.Map.SelectedMapSourceId = sourceId;
            project.Settings.Map.Center = new GeoCoordinate(22.3, 113.4);
            project.Settings.Map.Zoom = 14.5;
            project.Settings.Map.Bearing = 5;
            project.Settings.Map.Pitch = 25;
            var service = new ProjectFileService();

            await service.SaveAsync(project, path, true);
            var actual = await service.LoadAsync(path);

            Assert.Equal(sourceId, actual.Settings.Map.SelectedMapSourceId);
            Assert.Equal(new GeoCoordinate(22.3, 113.4), actual.Settings.Map.Center);
            Assert.Equal(14.5, actual.Settings.Map.Zoom);
            Assert.Equal(5, actual.Settings.Map.Bearing);
            Assert.Equal(25, actual.Settings.Map.Pitch);
        }
        finally { Directory.Delete(directory, true); }
    }
}
