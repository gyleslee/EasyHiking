using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using HikingGuide.Application.Projects;
using HikingGuide.Core.Models;

namespace HikingGuide.Infrastructure.Projects;

public sealed class AutoSaveService(
    IProjectFileService projectFileService,
    ApplicationDataPaths paths,
    TimeSpan? interval = null) : IAutoSaveService
{
    private sealed record AutoSaveMetadata(Guid ProjectId, string ProjectName, string? FormalProjectPath, DateTimeOffset SavedAt);
    private readonly SemaphoreSlim _gate = new(1, 1);
    public TimeSpan Interval { get; } = interval ?? TimeSpan.FromMinutes(5);

    public async Task<bool> SaveIfDirtyAsync(HikingProject? project, string? formalProjectPath, CancellationToken cancellationToken = default)
    {
        if (project is null || !project.IsDirty) return false;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!project.IsDirty) return false;
            Directory.CreateDirectory(paths.AutoSaveDirectory);
            var key = GetKey(project.Id, formalProjectPath);
            var archivePath = GetArchivePath(key);
            await projectFileService.SaveAsync(project, archivePath, createBackup: false, cancellationToken: cancellationToken);
            var metadata = new AutoSaveMetadata(project.Id, project.Name,
                string.IsNullOrWhiteSpace(formalProjectPath) ? null : Path.GetFullPath(formalProjectPath), DateTimeOffset.UtcNow);
            await WriteMetadataAsync(GetMetadataPath(key), metadata, cancellationToken);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            File.Delete(GetArchivePath(projectId));
            File.Delete(GetMetadataPath(projectId));
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteForPathAsync(Guid projectId, string? formalProjectPath,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var key = GetKey(projectId, formalProjectPath);
            File.Delete(GetArchivePath(key));
            File.Delete(GetMetadataPath(key));
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<AutoSaveRecoveryCandidate>> FindRecoveryCandidatesAsync(
        bool previousSessionEndedNormally,
        CancellationToken cancellationToken = default)
    {
        if (previousSessionEndedNormally || !Directory.Exists(paths.AutoSaveDirectory)) return [];
        var result = new List<AutoSaveRecoveryCandidate>();
        foreach (var metadataPath in Directory.EnumerateFiles(paths.AutoSaveDirectory, "*.meta.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var fileName = Path.GetFileName(metadataPath);
                var key = fileName[..^".meta.json".Length];
                await using var stream = File.OpenRead(metadataPath);
                var metadata = await JsonSerializer.DeserializeAsync<AutoSaveMetadata>(stream, cancellationToken: cancellationToken);
                if (metadata is null || !File.Exists(GetArchivePath(key))) continue;
                DateTimeOffset? formalTime = metadata.FormalProjectPath is not null && File.Exists(metadata.FormalProjectPath)
                    ? File.GetLastWriteTimeUtc(metadata.FormalProjectPath) : null;
                if (formalTime is null || metadata.SavedAt > formalTime)
                    result.Add(new AutoSaveRecoveryCandidate(metadata.ProjectId, metadata.ProjectName,
                        GetArchivePath(key), metadata.FormalProjectPath, metadata.SavedAt, formalTime));
            }
            catch (JsonException) { /* 单个损坏元数据不影响其它候选项。 */ }
        }
        return result.OrderByDescending(item => item.AutoSaveTime).ToArray();
    }

    public async Task<HikingProject> RecoverAsync(AutoSaveRecoveryCandidate candidate, CancellationToken cancellationToken = default)
    {
        var project = await projectFileService.LoadAsync(candidate.AutoSavePath, cancellationToken: cancellationToken);
        if (project.Id != candidate.ProjectId) throw new ProjectFileException("自动恢复数据与项目 ID 不一致。");
        project.MarkDirty();
        return project;
    }

    private static string GetKey(Guid id, string? formalProjectPath)
    {
        if (string.IsNullOrWhiteSpace(formalProjectPath)) return id.ToString("D");
        var canonical = Path.GetFullPath(formalProjectPath).ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return $"{id:D}-{Convert.ToHexString(hash)[..16]}";
    }

    private string GetArchivePath(Guid id) => GetArchivePath(id.ToString("D"));
    private string GetMetadataPath(Guid id) => GetMetadataPath(id.ToString("D"));
    private string GetArchivePath(string key) => Path.Combine(paths.AutoSaveDirectory, $"{key}.autosave.hikeproj");
    private string GetMetadataPath(string key) => Path.Combine(paths.AutoSaveDirectory, $"{key}.meta.json");

    private static async Task WriteMetadataAsync(string path, AutoSaveMetadata metadata, CancellationToken cancellationToken)
    {
        var temp = path + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
            await JsonSerializer.SerializeAsync(stream, metadata, cancellationToken: cancellationToken);
        File.Move(temp, path, true);
    }
}
