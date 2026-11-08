using HikingGuide.Core.Models;

namespace HikingGuide.App.ViewModels;

/// <summary>同一项目中的同步、可逆领域编辑；后台计算应在调用 Execute 前完成。</summary>
public sealed class ProjectEditHistory
{
    private readonly Stack<Edit> _undo = [];
    private readonly Stack<Edit> _redo = [];
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public HikingProject? Project { get; set; }
    public event EventHandler? Changed;

    public void Execute(string name, Action apply, Action revert)
    {
        var beforeRetired = Project?.RetiredGuideSegments.ToArray();
        apply();
        var afterRetired = Project?.RetiredGuideSegments.ToArray();
        _undo.Push(new Edit(name,
            () => { apply(); RestoreRetired(afterRetired); },
            () => { revert(); RestoreRetired(beforeRetired); }));
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private void RestoreRetired(GuideSegment[]? segments)
    {
        if (Project is null || segments is null) return;
        Project.RetiredGuideSegments.Clear();
        foreach (var segment in segments) Project.RetiredGuideSegments.Add(segment);
    }
    public bool Undo()
    {
        if (!_undo.TryPeek(out var edit)) return false;
        edit.Revert();
        _undo.Pop();
        _redo.Push(edit);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }
    public bool Redo()
    {
        if (!_redo.TryPeek(out var edit)) return false;
        edit.Apply();
        _redo.Pop();
        _undo.Push(edit);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }
    public void Clear()
    {
        _undo.Clear(); _redo.Clear(); Changed?.Invoke(this, EventArgs.Empty);
    }
    private sealed record Edit(string Name, Action Apply, Action Revert);
}
