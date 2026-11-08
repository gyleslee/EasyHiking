using HikingGuide.Application.TrackFiles;
using HikingGuide.Application.Projects;

namespace HikingGuide.App.Services;

public interface IFileDialogService
{
    IReadOnlyList<string> ShowOpenDialog(TrackFileFormat format);

    string? ShowSaveDialog(TrackFileFormat format, string defaultFileName);

    IReadOnlyList<string> ShowOpenProjectDialog();
    string? ShowSaveProjectDialog(string defaultFileName);
    NewProjectRequest? ShowNewProjectDialog();
    UnsavedChangesChoice ConfirmUnsavedChanges(string projectName);
    bool ConfirmRemoveMissingRecent(string filePath);
    RecoveryChoice ConfirmRecovery(AutoSaveRecoveryCandidate candidate);

    void ShowInformation(string title, string message);

    void ShowWarning(string title, string message);

    void ShowError(string title, string message);
}
