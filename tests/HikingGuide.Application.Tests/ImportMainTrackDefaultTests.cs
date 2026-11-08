using HikingGuide.Application.Guides;
using HikingGuide.Application.TrackFiles;
using HikingGuide.Core.Models;

namespace HikingGuide.Application.Tests;

public sealed class ImportMainTrackDefaultTests
{
    private static TrackImportResult Parsed() => new() { Tracks = {new Track { Name="导入轨迹", Segments=[new() { Points=[new() {Latitude=23,Longitude=113},new() {Latitude=23,Longitude=113.01}] }] }} };
    [Fact]
    public void NewProject_DefaultDoesNotSelectOrAssignMainTrack()
    {
        var project=new HikingProject(); var parsed=Parsed(); var session=ProjectImportSession.Create(parsed,"test.gpx",project);
        Assert.Null(session.SelectedGuideTrackId); Assert.Contains(parsed.Tracks[0].Id,session.SelectedTrackIds);
        var service=new ProjectImportService(); service.Apply(service.Prepare(session,project),project,false);
        Assert.Single(project.Tracks); Assert.Null(project.MainGuideTrackId);
    }
    [Fact]
    public void ExplicitMainSelectionStillWorks()
    {
        var project=new HikingProject(); var parsed=Parsed(); var session=ProjectImportSession.Create(parsed,"test.gpx",project);
        session.SelectedGuideTrackId=parsed.Tracks[0].Id; var service=new ProjectImportService();
        service.Apply(service.Prepare(session,project),project,false); Assert.Equal(parsed.Tracks[0].Id,project.MainGuideTrackId);
    }
    [Fact]
    public void ExistingProjectMainIsPreservedNotReplacedByImport()
    {
        var project=new HikingProject(); var existing=Parsed().Tracks[0]; project.AddTrack(existing);
        var session=ProjectImportSession.Create(Parsed(),"test.gpx",project); Assert.Equal(existing.Id,session.SelectedGuideTrackId);
        var service=new ProjectImportService(); service.Apply(service.Prepare(session,project),project,false); Assert.Equal(existing.Id,project.MainGuideTrackId);
    }
}
