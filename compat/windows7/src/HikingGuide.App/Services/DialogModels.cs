using HikingGuide.Application.Projects;

namespace HikingGuide.App.Services;

public sealed record NewProjectRequest(string Name, string FilePath);

public enum UnsavedChangesChoice
{
    Save,
    Discard,
    Cancel
}

public enum RecoveryChoice
{
    Recover,
    Ignore
}
