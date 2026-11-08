using HikingGuide.Core.Models;

namespace HikingGuide.Application.Projects;

public interface IProjectFileService
{
    Task SaveAsync(
        HikingProject project,
        string filePath,
        bool createBackup,
        IProgress<ProjectOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<HikingProject> LoadAsync(
        string filePath,
        IProgress<ProjectOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task ValidateArchiveAsync(string filePath, CancellationToken cancellationToken = default);
}
