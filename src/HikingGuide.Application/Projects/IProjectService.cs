using HikingGuide.Core.Models;

namespace HikingGuide.Application.Projects;

public interface IProjectService
{
    HikingProject? CurrentProject { get; }
    string? CurrentProjectPath { get; }
    IReadOnlyList<OpenProjectContext> OpenProjects { get; }
    event EventHandler? CurrentProjectChanged;
    HikingProject CreateNew(string name = "未命名项目");
    Task<HikingProject> OpenAsync(string filePath, IProgress<ProjectOperationProgress>? progress = null, CancellationToken cancellationToken = default);
    Task SaveAsync(IProgress<ProjectOperationProgress>? progress = null, CancellationToken cancellationToken = default);
    Task SaveAsAsync(string filePath, IProgress<ProjectOperationProgress>? progress = null, CancellationToken cancellationToken = default);
    void UseRecovered(HikingProject project, string? formalProjectPath);
    void Close();
    bool Activate(Guid contextId);
}
