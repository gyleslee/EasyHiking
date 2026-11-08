namespace HikingGuide.Application.Projects;

public sealed class ProjectMigrationRegistry(IEnumerable<IProjectMigration>? migrations = null)
{
    public const int CurrentProjectVersion = 3;
    private readonly IReadOnlyList<IProjectMigration> _migrations =
        (migrations ?? [new ProjectMigrationV1ToV2(), new ProjectMigrationV2ToV3()]).OrderBy(item => item.FromVersion).ToArray();

    public async Task ApplyAsync(ProjectMigrationContext context, CancellationToken cancellationToken = default)
    {
        if (context.ProjectVersion > CurrentProjectVersion)
        {
            throw new ProjectVersionException(
                "此项目由更新版本的软件创建。当前版本无法安全打开该项目，请升级软件后重试。");
        }

        while (context.ProjectVersion < CurrentProjectVersion)
        {
            var migration = _migrations.SingleOrDefault(item => item.FromVersion == context.ProjectVersion)
                ?? throw new ProjectVersionException($"缺少项目版本 {context.ProjectVersion} 的迁移程序，无法安全打开。");
            await migration.MigrateAsync(context, cancellationToken);
            context.ProjectVersion = migration.ToVersion;
        }
    }
}
