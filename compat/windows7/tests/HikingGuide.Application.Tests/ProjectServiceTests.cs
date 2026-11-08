using HikingGuide.Application.Projects;

namespace HikingGuide.Application.Tests;

public sealed class ProjectServiceTests
{
    [Fact]
    public void CreateNew_CreatesCleanCurrentVersionProject()
    {
        var service = new ProjectService();

        var project = service.CreateNew(" 环船百大纵走 ");

        Assert.Same(project, service.CurrentProject);
        Assert.Equal("环船百大纵走", project.Name);
        Assert.Equal(3, project.ProjectVersion);
        Assert.False(project.IsDirty);
        Assert.Equal(TimeSpan.Zero, project.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, project.ModifiedAt.Offset);
    }

    [Fact]
    public void WorkspaceKeepsSeveralProjectsAndIndependentDirtyState()
    {
        var service = new ProjectService();
        var first = service.CreateNew("甲项目");
        first.Description = "人工内容";
        var second = service.CreateNew("乙项目");
        Assert.Equal(2, service.OpenProjects.Count);
        Assert.Same(second, service.CurrentProject);
        Assert.True(first.IsDirty);
        Assert.False(second.IsDirty);
        Assert.True(service.Activate(service.OpenProjects.First(item => ReferenceEquals(item.Project, first)).ContextId));
        Assert.Same(first, service.CurrentProject);
        Assert.Equal("人工内容", service.CurrentProject!.Description);
        service.Close();
        Assert.Single(service.OpenProjects);
        Assert.Same(second, service.CurrentProject);
    }
}
