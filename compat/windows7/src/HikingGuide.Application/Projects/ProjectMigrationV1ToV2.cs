using System.Text.Json;
using System.Text.Json.Nodes;

namespace HikingGuide.Application.Projects;

/// <summary>只升级内存中的归档条目；原 V1 文件保持原样直到用户另存。</summary>
public sealed class ProjectMigrationV1ToV2 : IProjectMigration
{
    public int FromVersion => 1;
    public int ToVersion => 2;

    public Task MigrateAsync(ProjectMigrationContext context, CancellationToken cancellationToken = default)
    {
        foreach (var entry in new[] { "manifest.json", "project.json" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!context.Entries.TryGetValue(entry, out var bytes))
                throw new ProjectFileException($"旧项目缺少 {entry}，无法升级。");
            var root = JsonNode.Parse(bytes)?.AsObject() ?? throw new ProjectFileException($"旧项目的 {entry} 无效。");
            root["projectVersion"] = ToVersion;
            context.Entries[entry] = JsonSerializer.SerializeToUtf8Bytes(root);
        }
        return Task.CompletedTask;
    }
}
