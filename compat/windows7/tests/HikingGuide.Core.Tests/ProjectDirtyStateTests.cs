using HikingGuide.Core.Models;

namespace HikingGuide.Core.Tests;

public sealed class ProjectDirtyStateTests
{
    [Fact]
    public void ChangingProjectName_MarksProjectDirty()
    {
        var project = CleanProject();

        project.Name = "新名称";

        Assert.True(project.IsDirty);
    }

    [Fact]
    public void AddingTrack_MarksProjectDirty()
    {
        var project = CleanProject();

        project.AddTrack(new Track());

        Assert.True(project.IsDirty);
    }

    [Fact]
    public void AddingAndRemovingGuideNode_MarksProjectDirty()
    {
        var project = CleanProject();
        var node = new GuideNode();

        project.GuideNodes.Add(node);
        Assert.True(project.IsDirty);

        project.MarkSaved();
        project.GuideNodes.Remove(node);
        Assert.True(project.IsDirty);
    }

    [Fact]
    public void UpdatingManualData_MarksProjectDirty()
    {
        var project = CleanProject();

        project.UpdateManualData(data => data.Notes = "人工填写内容");

        Assert.True(project.IsDirty);
        Assert.Equal("人工填写内容", project.ManualData.Notes);
    }

    [Fact]
    public void NotifyingInPlaceTrackChange_MarksProjectDirty()
    {
        var project = CleanProject();

        project.NotifyTrackChanged();

        Assert.True(project.IsDirty);
    }

    private static HikingProject CleanProject()
    {
        var project = new HikingProject();
        project.MarkSaved();
        return project;
    }
}
