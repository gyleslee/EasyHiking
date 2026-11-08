namespace HikingGuide.Application.Projects;

public interface IRecentProjectService
{
    Task<IReadOnlyList<RecentProjectEntry>> GetAsync(CancellationToken cancellationToken = default);
    Task AddAsync(string filePath, string projectName, CancellationToken cancellationToken = default);
    Task RemoveAsync(string filePath, CancellationToken cancellationToken = default);
}
