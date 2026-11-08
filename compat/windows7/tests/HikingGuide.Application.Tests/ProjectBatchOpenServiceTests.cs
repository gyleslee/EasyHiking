using HikingGuide.Application.Projects;
using HikingGuide.Core.Models;

namespace HikingGuide.Application.Tests;

public sealed class ProjectBatchOpenServiceTests
{
    [Fact]
    public async Task EmptyWorkspace_OpensThreeFiles_AndKeepsFirstActive()
    {
        var loader = new FakeFiles();
        var workspace = new ProjectService(loader);
        var paths = new[] { "A.hikeproj", "B.hikeproj", "C.hikeproj" }
            .Select(Path.GetFullPath).ToArray();
        var result = await new ProjectBatchOpenService(workspace).OpenAsync(paths);
        Assert.Equal(3, result.OpenedCount);
        Assert.Equal(3, workspace.OpenProjects.Count);
        Assert.Equal("A", workspace.CurrentProject?.Name);
        Assert.All(workspace.OpenProjects, item => Assert.False(item.IsDirty));
        Assert.All(result.Items, item =>
        {
            Assert.NotEqual(Guid.Empty, item.LoadRequestId);
            Assert.NotNull(item.ProjectId);
        });
        Assert.Equal(3, result.Items.Select(item => item.LoadRequestId).Distinct().Count());
    }

    [Fact]
    public async Task ExistingActiveAndDirtyProject_ArePreserved_WhileFailureDoesNotStopBatch()
    {
        var loader = new FakeFiles();
        var workspace = new ProjectService(loader);
        var firstPath = Path.GetFullPath("A.hikeproj");
        var original = await workspace.OpenAsync(firstPath);
        original.Description = "人工填写，尚未保存";
        var paths = new[] { firstPath, Path.GetFullPath("broken.hikeproj"),
            Path.GetFullPath("B.hikeproj"), Path.GetFullPath("B.hikeproj") };
        var result = await new ProjectBatchOpenService(workspace).OpenAsync(paths);
        Assert.Equal(1, result.OpenedCount);
        Assert.Equal(1, result.AlreadyOpenCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(2, workspace.OpenProjects.Count);
        Assert.Same(original, workspace.CurrentProject);
        Assert.True(original.IsDirty);
        Assert.Equal("人工填写，尚未保存", original.Description);
        Assert.Equal(1, loader.LoadCounts[firstPath]);
    }

    [Fact]
    public async Task DotSegmentsAndCaseVariants_AreDeduplicatedByAbsolutePath()
    {
        var loader = new FakeFiles();
        var workspace = new ProjectService(loader);
        var path = Path.GetFullPath("Route.hikeproj");
        var alternative = Path.Combine(Path.GetDirectoryName(path)!, ".", "ROUTE.hikeproj");
        var result = await new ProjectBatchOpenService(workspace).OpenAsync([path, alternative]);
        Assert.Equal(1, result.OpenedCount);
        Assert.Single(workspace.OpenProjects);
    }

    private sealed class FakeFiles : IProjectFileService
    {
        public Dictionary<string, int> LoadCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Task<HikingProject> LoadAsync(string filePath,
            IProgress<ProjectOperationProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            LoadCounts[filePath] = LoadCounts.GetValueOrDefault(filePath) + 1;
            if (Path.GetFileName(filePath).StartsWith("broken", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("测试文件已损坏");
            return Task.FromResult(new HikingProject { Name = Path.GetFileNameWithoutExtension(filePath) });
        }
        public Task SaveAsync(HikingProject project, string filePath, bool createBackup,
            IProgress<ProjectOperationProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task ValidateArchiveAsync(string filePath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
