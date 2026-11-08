using System.Text.Json;
using HikingGuide.Application.Projects;

namespace HikingGuide.Infrastructure.Projects;

public sealed class RecentProjectService(ApplicationDataPaths paths) : IRecentProjectService
{
    private const int MaximumEntries = 10;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<RecentProjectEntry>> GetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await ReadUnsafeAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    public async Task AddAsync(string filePath, string projectName, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var fullPath = Path.GetFullPath(filePath);
            var entries = (await ReadUnsafeAsync(cancellationToken))
                .Where(item => !string.Equals(item.FilePath, fullPath, StringComparison.OrdinalIgnoreCase)).ToList();
            entries.Insert(0, new RecentProjectEntry(fullPath, projectName, DateTimeOffset.UtcNow));
            await WriteUnsafeAsync(entries.Take(MaximumEntries).ToArray(), cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var fullPath = Path.GetFullPath(filePath);
            var entries = (await ReadUnsafeAsync(cancellationToken))
                .Where(item => !string.Equals(item.FilePath, fullPath, StringComparison.OrdinalIgnoreCase)).ToArray();
            await WriteUnsafeAsync(entries, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private async Task<List<RecentProjectEntry>> ReadUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.RecentProjectsFile)) return [];
        await using var stream = new FileStream(paths.RecentProjectsFile, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        return await JsonSerializer.DeserializeAsync<List<RecentProjectEntry>>(stream, Options, cancellationToken) ?? [];
    }

    private async Task WriteUnsafeAsync(IReadOnlyCollection<RecentProjectEntry> entries, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.RootDirectory);
        var temp = paths.RecentProjectsFile + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
            await JsonSerializer.SerializeAsync(stream, entries, Options, cancellationToken);
        File.Move(temp, paths.RecentProjectsFile, true);
    }
}
