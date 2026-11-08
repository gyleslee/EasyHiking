namespace HikingGuide.Application.Projects;

public interface IAppSessionStateService
{
    Task<bool> BeginSessionAsync(CancellationToken cancellationToken = default);
    Task MarkCleanShutdownAsync(CancellationToken cancellationToken = default);
}
