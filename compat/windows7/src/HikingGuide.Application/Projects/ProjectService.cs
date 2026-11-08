using HikingGuide.Core.Models;

namespace HikingGuide.Application.Projects;

public sealed class ProjectService(
    IProjectFileService? projectFileService = null,
    IRecentProjectService? recentProjectService = null,
    IAutoSaveService? autoSaveService = null) : IProjectService
{
    private readonly List<OpenProjectContext> _openProjects = [];
    private OpenProjectContext? _active;
    public IReadOnlyList<OpenProjectContext> OpenProjects => _openProjects;
    public HikingProject? CurrentProject => _active?.Project;
    public string? CurrentProjectPath => _active?.FilePath;
    public event EventHandler? CurrentProjectChanged;

    public HikingProject CreateNew(string name = "未命名项目")
    {
        var project = new HikingProject
        {
            Name = string.IsNullOrWhiteSpace(name) ? "未命名项目" : name.Trim()
        };
        project.MarkSaved();
        _active = new OpenProjectContext(project, null);
        _openProjects.Add(_active);
        CurrentProjectChanged?.Invoke(this, EventArgs.Empty);
        return project;
    }

    public async Task<HikingProject> OpenAsync(
        string filePath,
        IProgress<ProjectOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var fileService = projectFileService ?? throw new InvalidOperationException("未配置项目文件服务。");
        var fullPath = ProjectPathNormalizer.Normalize(filePath);
        var existing = _openProjects.FirstOrDefault(context => context.FilePath is not null &&
            ProjectPathNormalizer.Normalize(context.FilePath).Equals(fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _active = existing;
            CurrentProjectChanged?.Invoke(this, EventArgs.Empty);
            return existing.Project;
        }
        var project = await Task.Run(() => fileService.LoadAsync(fullPath, progress, cancellationToken),
            cancellationToken);
        _active = new OpenProjectContext(project, fullPath);
        _openProjects.Add(_active);
        project.MarkSaved();
        if (recentProjectService is not null)
            await recentProjectService.AddAsync(fullPath, project.Name, cancellationToken);
        CurrentProjectChanged?.Invoke(this, EventArgs.Empty);
        return project;
    }

    public Task SaveAsync(
        IProgress<ProjectOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (CurrentProjectPath is null)
            throw new InvalidOperationException("项目尚未指定保存位置，请使用“项目另存为”。");
        return SaveCoreAsync(CurrentProjectPath, progress, cancellationToken);
    }

    public Task SaveAsAsync(
        string filePath,
        IProgress<ProjectOperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        SaveCoreAsync(Path.GetFullPath(filePath), progress, cancellationToken);

    public void Close()
    {
        if (_active is not null) _openProjects.Remove(_active);
        _active = _openProjects.LastOrDefault();
        CurrentProjectChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool Activate(Guid contextId)
    {
        var context = _openProjects.FirstOrDefault(item => item.ContextId == contextId);
        if (context is null) return false;
        _active = context;
        CurrentProjectChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void UseRecovered(HikingProject project, string? formalProjectPath)
    {
        ArgumentNullException.ThrowIfNull(project);
        _active = new OpenProjectContext(project,
            string.IsNullOrWhiteSpace(formalProjectPath) ? null : Path.GetFullPath(formalProjectPath));
        _openProjects.Add(_active);
        project.MarkDirty();
        CurrentProjectChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task SaveCoreAsync(
        string filePath,
        IProgress<ProjectOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var project = CurrentProject ?? throw new InvalidOperationException("当前没有打开的项目。");
        var previousPath = CurrentProjectPath;
        if (_openProjects.Any(context => !ReferenceEquals(context, _active) && context.FilePath is not null &&
            Path.GetFullPath(context.FilePath).Equals(filePath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("该项目文件已由工作区内的另一项目打开，不能覆盖保存。");
        var fileService = projectFileService ?? throw new InvalidOperationException("未配置项目文件服务。");
        await fileService.SaveAsync(project, filePath, createBackup: true, progress, cancellationToken);
        _active!.FilePath = filePath;
        project.MarkSaved();
        if (autoSaveService is not null)
        {
            try { await autoSaveService.DeleteForPathAsync(project.Id, previousPath, cancellationToken); }
            catch when (!cancellationToken.IsCancellationRequested) { /* 正式项目已安全保存，清理失败不回滚保存结果。 */ }
        }
        if (recentProjectService is not null)
        {
            try { await recentProjectService.AddAsync(filePath, project.Name, cancellationToken); }
            catch when (!cancellationToken.IsCancellationRequested) { /* 最近项目列表故障不影响正式文件。 */ }
        }
        CurrentProjectChanged?.Invoke(this, EventArgs.Empty);
    }
}
