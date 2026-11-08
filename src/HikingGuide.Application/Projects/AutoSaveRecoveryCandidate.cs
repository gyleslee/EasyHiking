using HikingGuide.Core.Models;

namespace HikingGuide.Application.Projects;

public sealed record AutoSaveRecoveryCandidate(
    Guid ProjectId,
    string ProjectName,
    string AutoSavePath,
    string? FormalProjectPath,
    DateTimeOffset AutoSaveTime,
    DateTimeOffset? FormalSaveTime);

public interface IAutoSaveService
{
    TimeSpan Interval { get; }
    Task<bool> SaveIfDirtyAsync(HikingProject? project, string? formalProjectPath, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task DeleteForPathAsync(Guid projectId, string? formalProjectPath,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AutoSaveRecoveryCandidate>> FindRecoveryCandidatesAsync(bool previousSessionEndedNormally, CancellationToken cancellationToken = default);
    Task<HikingProject> RecoverAsync(AutoSaveRecoveryCandidate candidate, CancellationToken cancellationToken = default);
}
