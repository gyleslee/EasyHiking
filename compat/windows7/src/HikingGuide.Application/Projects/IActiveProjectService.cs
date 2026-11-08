namespace HikingGuide.Application.Projects;

/// <summary>记录上次仍处于打开状态的正式项目；与最近项目历史无关。</summary>
public interface IActiveProjectService
{
    Task<string?> GetAsync(CancellationToken cancellationToken = default);
    Task SetAsync(string? projectPath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetOpenPathsAsync(CancellationToken cancellationToken = default);
    Task SetWorkspaceAsync(IReadOnlyList<string> openPaths, string? activePath,
        CancellationToken cancellationToken = default);
}
