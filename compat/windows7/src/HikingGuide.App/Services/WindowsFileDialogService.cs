using System.IO;
using System.Windows;
using HikingGuide.Application.TrackFiles;
using HikingGuide.Application.Projects;
using Microsoft.Win32;

namespace HikingGuide.App.Services;

public sealed class WindowsFileDialogService : IFileDialogService
{
    public IReadOnlyList<string> ShowOpenDialog(TrackFileFormat format)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入轨迹与标注",
            Filter = "支持的轨迹文件 (*.gpx;*.kml;*.kmz)|*.gpx;*.kml;*.kmz|GPX 文件 (*.gpx)|*.gpx|KML 文件 (*.kml)|*.kml|KMZ 文件 (*.kmz)|*.kmz|所有文件 (*.*)|*.*",
            FilterIndex = format switch { TrackFileFormat.Gpx => 2, TrackFileFormat.Kml => 3, TrackFileFormat.Kmz => 4, _ => 1 },
            CheckFileExists = true,
            Multiselect = true
        };
        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }

    public string? ShowSaveDialog(TrackFileFormat format, string defaultFileName)
    {
        var extension = format switch { TrackFileFormat.Gpx => ".gpx", TrackFileFormat.Kml => ".kml", _ => ".kmz" };
        var dialog = new SaveFileDialog
        {
            Title = "合并导出轨迹与标注",
            Filter = format switch
            {
                TrackFileFormat.Gpx => "GPX 文件 (*.gpx)|*.gpx",
                TrackFileFormat.Kml => "KML 文件 (*.kml)|*.kml",
                _ => "KMZ 文件 (*.kmz)|*.kmz"
            },
            DefaultExt = extension,
            AddExtension = true,
            FileName = SanitizeFileName(defaultFileName) + extension,
            OverwritePrompt = true
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public IReadOnlyList<string> ShowOpenProjectDialog()
    {
        var dialog = new OpenFileDialog
        {
            Title = "打开徒步攻略项目",
            Filter = "徒步攻略项目 (*.hikeproj)|*.hikeproj",
            CheckFileExists = true,
            Multiselect = true
        };
        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }

    public string? ShowSaveProjectDialog(string defaultFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "项目另存为",
            Filter = "徒步攻略项目 (*.hikeproj)|*.hikeproj",
            DefaultExt = ".hikeproj",
            AddExtension = true,
            FileName = SanitizeFileName(defaultFileName) + ".hikeproj",
            OverwritePrompt = true
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public NewProjectRequest? ShowNewProjectDialog()
    {
        var window = new NewProjectWindow();
        return window.ShowDialog() == true ? window.Result : null;
    }

    public UnsavedChangesChoice ConfirmUnsavedChanges(string projectName)
    {
        var result = MessageBox.Show(
            $"“{projectName}”有尚未保存的修改。\n\n是否保存修改？",
            "未保存的修改",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);
        return result switch
        {
            MessageBoxResult.Yes => UnsavedChangesChoice.Save,
            MessageBoxResult.No => UnsavedChangesChoice.Discard,
            _ => UnsavedChangesChoice.Cancel
        };
    }

    public bool ConfirmRemoveMissingRecent(string filePath) => MessageBox.Show(
        $"找不到该项目文件：\n{filePath}\n\n是否从“最近打开的项目”中移除？",
        "项目不存在",
        MessageBoxButton.YesNo,
        MessageBoxImage.Question) == MessageBoxResult.Yes;

    public RecoveryChoice ConfirmRecovery(AutoSaveRecoveryCandidate candidate)
    {
        var formal = candidate.FormalSaveTime?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "尚未正式保存";
        var result = MessageBox.Show(
            $"检测到未保存的项目恢复数据\n\n项目：{candidate.ProjectName}\n" +
            $"自动保存时间：{candidate.AutoSaveTime.ToLocalTime():yyyy-MM-dd HH:mm}\n正式保存时间：{formal}\n\n是否恢复？",
            "自动恢复",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        return result == MessageBoxResult.Yes ? RecoveryChoice.Recover : RecoveryChoice.Ignore;
    }

    public void ShowInformation(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowWarning(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public void ShowError(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    private static string SanitizeFileName(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(character =>
            invalidCharacters.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "导出轨迹" : sanitized;
    }
}
