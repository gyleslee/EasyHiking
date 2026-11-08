using HikingGuide.Core.Models;

namespace HikingGuide.Core.Tests;

public sealed class MultiTrackProjectTests
{
    [Fact]
    public void FirstTrack_BecomesActive_AdditionalTrackDoesNotReplaceIt()
    {
        var project = new HikingProject();
        var first = new Track { Name = "主线" };
        var second = new Track { Name = "备用线" };

        project.AddTrack(first);
        project.AddTrack(second);

        Assert.Equal(first.Id, project.ActiveTrackId);
        Assert.Same(first, project.ActiveTrack);
    }

    [Fact]
    public void RemovingActiveTrack_SelectsRemainingTrack()
    {
        var project = new HikingProject();
        var first = new Track();
        var second = new Track();
        project.AddTrack(first); project.AddTrack(second); project.SetActiveTrack(second.Id);

        Assert.True(project.RemoveTrack(second.Id));

        Assert.Equal(first.Id, project.ActiveTrackId);
    }

    [Fact]
    public void RemovingLastTrack_ClearsActiveTrack()
    {
        var project = new HikingProject();
        var track = new Track();
        project.AddTrack(track);

        project.RemoveTrack(track.Id);

        Assert.Null(project.ActiveTrackId);
        Assert.Null(project.ActiveTrack);
    }

    [Fact]
    public void SetActiveTrack_RejectsForeignId()
    {
        var project = new HikingProject();
        Assert.Throws<ArgumentException>(() => project.SetActiveTrack(Guid.NewGuid()));
    }

    [Fact]
    public void ChangingSelectedTrack_DoesNotMarkProjectDirtyOrChangeMain()
    {
        var project = new HikingProject();
        var first = new Track(); var second = new Track();
        project.AddTrack(first); project.AddTrack(second); project.MarkSaved();

        project.SetActiveTrack(second.Id);

        Assert.False(project.IsDirty);
        Assert.Equal(first.Id, project.MainGuideTrackId);
    }
}
