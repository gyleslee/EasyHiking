namespace HikingGuide.Application.Projects;

public interface IProjectMigration
{
    int FromVersion { get; }
    int ToVersion { get; }
    Task MigrateAsync(ProjectMigrationContext context, CancellationToken cancellationToken = default);
}

public sealed class ProjectMigrationContext
{
    public required IDictionary<string, byte[]> Entries { get; init; }
    public required int ProjectVersion { get; set; }
}
