using System.Text.Json;
using HikingGuide.Application.Projects;

namespace HikingGuide.Infrastructure.Projects;

public sealed class AppSessionStateService(ApplicationDataPaths paths) : IAppSessionStateService
{
    private sealed record State(bool CleanShutdown, DateTimeOffset StartedAt);

    public async Task<bool> BeginSessionAsync(CancellationToken cancellationToken = default)
    {
        var previousEndedNormally = true;
        if (File.Exists(paths.SessionStateFile))
        {
            try
            {
                await using var read = File.OpenRead(paths.SessionStateFile);
                previousEndedNormally = (await JsonSerializer.DeserializeAsync<State>(read, cancellationToken: cancellationToken))?.CleanShutdown ?? true;
            }
            catch (JsonException) { previousEndedNormally = false; }
        }

        await WriteAsync(new State(false, DateTimeOffset.UtcNow), cancellationToken);
        return previousEndedNormally;
    }

    public Task MarkCleanShutdownAsync(CancellationToken cancellationToken = default) =>
        WriteAsync(new State(true, DateTimeOffset.UtcNow), cancellationToken);

    private async Task WriteAsync(State state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.RootDirectory);
        var temp = paths.SessionStateFile + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
            await JsonSerializer.SerializeAsync(stream, state, cancellationToken: cancellationToken);
        File.Move(temp, paths.SessionStateFile, true);
    }
}
