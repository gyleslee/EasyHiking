using HikingGuide.Core.Models;

namespace HikingGuide.Application.Projects;

public sealed record ProjectOpenItem(Guid LoadRequestId, string FilePath, Guid? ProjectId,
    bool WasAlreadyOpen, string? Error);

public sealed record ProjectBatchOpenResult(IReadOnlyList<ProjectOpenItem> Items)
{
    public int OpenedCount => Items.Count(item => item.ProjectId.HasValue && !item.WasAlreadyOpen);
    public int AlreadyOpenCount => Items.Count(item => item.WasAlreadyOpen);
    public int FailedCount => Items.Count(item => item.Error is not null);
}

/// <summary>顺序后台读取大型项目；每个文件携带请求 ID、规范化路径和结果项目 ID。</summary>
public sealed class ProjectBatchOpenService(IProjectService workspace)
{
    public async Task<ProjectBatchOpenResult> OpenAsync(IEnumerable<string> paths,
        IProgress<ProjectOperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var activeBefore = workspace.OpenProjects.FirstOrDefault(item =>
            ReferenceEquals(item.Project, workspace.CurrentProject));
        var items = new List<ProjectOpenItem>();
        try
        {
            foreach (var path in paths.Select(ProjectPathNormalizer.Normalize).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requestId = Guid.NewGuid();
                var existing = workspace.OpenProjects.FirstOrDefault(item => item.FilePath is not null &&
                    ProjectPathNormalizer.Normalize(item.FilePath).Equals(path, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    items.Add(new ProjectOpenItem(requestId, path, existing.ProjectId, true, null));
                    continue;
                }
                try
                {
                    var project = await workspace.OpenAsync(path, progress, cancellationToken);
                    items.Add(new ProjectOpenItem(requestId, path, project.Id, false, null));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    items.Add(new ProjectOpenItem(requestId, path, null, false, error.Message));
                }
            }
        }
        finally
        {
            if (activeBefore is not null) workspace.Activate(activeBefore.ContextId);
            else if (items.FirstOrDefault(item => item.ProjectId.HasValue && !item.WasAlreadyOpen) is { } first)
            {
                var context = workspace.OpenProjects.FirstOrDefault(item => item.ProjectId == first.ProjectId);
                if (context is not null) workspace.Activate(context.ContextId);
            }
        }
        return new ProjectBatchOpenResult(items);
    }
}
