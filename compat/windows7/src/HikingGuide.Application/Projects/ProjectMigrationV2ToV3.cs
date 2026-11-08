using System.Text.Json;
using System.Text.Json.Nodes;

namespace HikingGuide.Application.Projects;

/// <summary>旧项目仅在内存中升级；原归档直到用户明确保存前保持不变。</summary>
public sealed class ProjectMigrationV2ToV3 : IProjectMigration
{
    public int FromVersion => 2;
    public int ToVersion => 3;

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
        context.Entries.TryAdd("media/manifest.json", "{\"blobs\":[],\"attachments\":[],\"externalReferences\":[]}"u8.ToArray());
        return Task.CompletedTask;
    }
}
