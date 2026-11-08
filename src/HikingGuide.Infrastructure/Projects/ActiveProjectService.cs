using System.Text.Json;
using HikingGuide.Application.Projects;

namespace HikingGuide.Infrastructure.Projects;

public sealed class ActiveProjectService(ApplicationDataPaths paths) : IActiveProjectService
{
    private sealed record State(string? ProjectPath, List<string>? OpenProjectPaths = null);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string?> GetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(paths.ActiveProjectFile)) return null;
            await using var stream = new FileStream(paths.ActiveProjectFile, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, true);
            var state = await JsonSerializer.DeserializeAsync<State>(stream, cancellationToken: cancellationToken);
            return string.IsNullOrWhiteSpace(state?.ProjectPath) ? null : Path.GetFullPath(state.ProjectPath);
        }
        catch (JsonException) { return null; }
        catch (ArgumentException) { return null; }
        finally { _gate.Release(); }
    }

    public async Task SetAsync(string? projectPath, CancellationToken cancellationToken = default)
        => await SetWorkspaceAsync(projectPath is null ? [] : [projectPath], projectPath, cancellationToken);

    public async Task<IReadOnlyList<string>> GetOpenPathsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(paths.ActiveProjectFile)) return [];
            await using var stream = new FileStream(paths.ActiveProjectFile, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, true);
            var state = await JsonSerializer.DeserializeAsync<State>(stream, cancellationToken: cancellationToken);
            return (state?.OpenProjectPaths ?? (state?.ProjectPath is null ? [] : [state.ProjectPath]))
                .Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (JsonException) { return []; }
        catch (ArgumentException) { return []; }
        finally { _gate.Release(); }
    }

    public async Task SetWorkspaceAsync(IReadOnlyList<string> openPaths, string? activePath,
        CancellationToken cancellationToken = default)
    {
        var normalized = string.IsNullOrWhiteSpace(activePath) ? null : Path.GetFullPath(activePath);
        var allPaths = openPaths.Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(paths.RootDirectory);
            var temporary = paths.ActiveProjectFile + ".tmp";
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write,
                FileShare.None, 4096, true))
                await JsonSerializer.SerializeAsync(stream, new State(normalized, allPaths), cancellationToken: cancellationToken);
            File.Move(temporary, paths.ActiveProjectFile, true);
        }
        finally { _gate.Release(); }
    }
}
