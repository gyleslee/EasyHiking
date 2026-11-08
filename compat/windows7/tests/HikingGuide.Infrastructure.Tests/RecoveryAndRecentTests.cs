using HikingGuide.Application.Projects;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Projects;

namespace HikingGuide.Infrastructure.Tests;

public sealed class RecoveryAndRecentTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "HikingGuide.P3.StateTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AutoSave_OnlyWritesDirtyProjectAndKeepsDirtyState()
    {
        var paths = new ApplicationDataPaths(_directory);
        var service = new AutoSaveService(new ProjectFileService(), paths);
        var project = new HikingProject { Name = "恢复测试" };
        project.MarkSaved();
        Assert.False(await service.SaveIfDirtyAsync(project, null));
        project.Description = "尚未正式保存";

        Assert.True(await service.SaveIfDirtyAsync(project, null));
        Assert.True(project.IsDirty);
        Assert.Single(Directory.EnumerateFiles(paths.AutoSaveDirectory, "*.autosave.hikeproj"));
    }

    [Fact]
    public async Task Recovery_RequiresAbnormalSessionAndMarksProjectDirty()
    {
        var paths = new ApplicationDataPaths(_directory);
        var service = new AutoSaveService(new ProjectFileService(), paths);
        var project = new HikingProject { Name = "异常恢复" };
        project.MarkSaved(); project.Description = "未保存修改";
        await service.SaveIfDirtyAsync(project, null);

        Assert.Empty(await service.FindRecoveryCandidatesAsync(previousSessionEndedNormally: true));
        var candidate = Assert.Single(await service.FindRecoveryCandidatesAsync(previousSessionEndedNormally: false));
        var restored = await service.RecoverAsync(candidate);
        Assert.True(restored.IsDirty);
        Assert.Equal(project.Id, restored.Id);
    }

    [Fact]
    public async Task FormalSave_DeletesMatchingAutoSaveAndMarksClean()
    {
        var paths = new ApplicationDataPaths(_directory);
        var files = new ProjectFileService();
        var autoSave = new AutoSaveService(files, paths);
        var recent = new RecentProjectService(paths);
        var projects = new ProjectService(files, recent, autoSave);
        var project = projects.CreateNew("正式保存");
        project.Description = "修改";
        await autoSave.SaveIfDirtyAsync(project, null);

        await projects.SaveAsAsync(Path.Combine(_directory, "正式保存.hikeproj"));

        Assert.False(project.IsDirty);
        Assert.Empty(Directory.EnumerateFiles(paths.AutoSaveDirectory, "*.autosave.hikeproj"));
    }

    [Fact]
    public async Task RecentProjects_KeepsTenNewestAndSupportsRemoval()
    {
        var service = new RecentProjectService(new ApplicationDataPaths(_directory));
        for (var index = 0; index < 12; index++) await service.AddAsync(Path.Combine(_directory, $"{index}.hikeproj"), $"项目{index}");

        var entries = await service.GetAsync();
        Assert.Equal(10, entries.Count);
        Assert.Equal("项目11", entries[0].ProjectName);
        await service.RemoveAsync(entries[0].FilePath);
        Assert.Equal(9, (await service.GetAsync()).Count);
    }

    [Fact]
    public async Task SessionState_DetectsUncleanThenCleanShutdown()
    {
        var service = new AppSessionStateService(new ApplicationDataPaths(_directory));
        Assert.True(await service.BeginSessionAsync());
        Assert.False(await service.BeginSessionAsync());
        await service.MarkCleanShutdownAsync();
        Assert.True(await service.BeginSessionAsync());
    }

    [Fact]
    public async Task ActiveProject_ReopensAfterAppRestartButManualCloseClearsIt()
    {
        var paths = new ApplicationDataPaths(_directory);
        var projectPath = Path.Combine(_directory, "徒步项目.hikeproj");
        await new ActiveProjectService(paths).SetAsync(projectPath);
        Assert.Equal(Path.GetFullPath(projectPath), await new ActiveProjectService(paths).GetAsync());

        await new ActiveProjectService(paths).SetAsync(null);
        Assert.Null(await new ActiveProjectService(paths).GetAsync());
    }

    [Fact]
    public async Task WorkspaceStateKeepsOnlyOpenPathsAndActiveSelection()
    {
        var paths = new ApplicationDataPaths(_directory);
        var first = Path.Combine(_directory, "甲.hikeproj");
        var second = Path.Combine(_directory, "乙.hikeproj");
        await new ActiveProjectService(paths).SetWorkspaceAsync([first, second], second);
        var restarted = new ActiveProjectService(paths);
        Assert.Equal(2, (await restarted.GetOpenPathsAsync()).Count);
        Assert.Equal(Path.GetFullPath(second), await restarted.GetAsync());
        await restarted.SetWorkspaceAsync([first], first);
        Assert.Equal([Path.GetFullPath(first)], await restarted.GetOpenPathsAsync());
    }

    [Fact]
    public async Task AutoSaveKeepsCopiedProjectsWithSameProjectIdSeparateByPath()
    {
        var paths = new ApplicationDataPaths(_directory);
        var storage = new ProjectFileService();
        var service = new AutoSaveService(storage, paths);
        var sharedId = Guid.NewGuid();
        var first = new HikingGuide.Core.Models.HikingProject { Id = sharedId, Name = "甲副本" };
        var second = new HikingGuide.Core.Models.HikingProject { Id = sharedId, Name = "乙副本" };
        first.Description = "甲的人工内容";
        second.Description = "乙的人工内容";
        var firstPath = Path.Combine(_directory, "甲.hikeproj");
        var secondPath = Path.Combine(_directory, "乙.hikeproj");
        Assert.True(await service.SaveIfDirtyAsync(first, firstPath));
        Assert.True(await service.SaveIfDirtyAsync(second, secondPath));
        var candidates = await service.FindRecoveryCandidatesAsync(false);
        Assert.Equal(2, candidates.Count);
        Assert.Equal(2, candidates.Select(item => item.AutoSavePath).Distinct().Count());
        Assert.Contains(candidates, item => item.FormalProjectPath == Path.GetFullPath(firstPath));
        Assert.Contains(candidates, item => item.FormalProjectPath == Path.GetFullPath(secondPath));
        await service.DeleteForPathAsync(sharedId, firstPath);
        Assert.Single(await service.FindRecoveryCandidatesAsync(false));
    }

    [Fact]
    public async Task CorruptActiveProjectStateDoesNotBlockStartup()
    {
        var paths = new ApplicationDataPaths(_directory);
        Directory.CreateDirectory(paths.RootDirectory);
        await File.WriteAllTextAsync(paths.ActiveProjectFile, "not json");
        Assert.Null(await new ActiveProjectService(paths).GetAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
