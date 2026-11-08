using HikingGuide.Core.Models;

namespace HikingGuide.Application.Projects;

/// <summary>同时打开的项目各自持有文件路径与运行时选择；领域项目不依赖工作区。</summary>
public sealed class OpenProjectContext(HikingProject project, string? filePath)
{
    public Guid ContextId { get; } = Guid.NewGuid();
    public HikingProject Project { get; } = project;
    public Guid ProjectId => Project.Id;
    public string? FilePath { get; internal set; } = filePath;
    public Guid? SelectedTrackId { get; set; }
    public Guid? SelectedObjectId { get; set; }
    public bool IsDirty => Project.IsDirty;
}
