using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using HikingGuide.App.Services;
using HikingGuide.App.ViewModels;
using HikingGuide.Application.Projects;
using HikingGuide.Application.TrackFiles;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Projects;

namespace HikingGuide.App;

public partial class App
{
    // 只由已在启动入口检查隔离目录的生命周期测试开关创建，不替代正常用户的对话框或自动保存。
    private sealed class LifecycleDialogs : IFileDialogService
    {
        public Queue<UnsavedChangesChoice> Choices { get; } = new();
        public List<string> Prompts { get; } = [];
        public Action? Prompted { get; set; }
        public string? SavePath { get; set; }
        public int Errors { get; private set; }
        public UnsavedChangesChoice ConfirmUnsavedChanges(string name)
        { Prompts.Add(name); Prompted?.Invoke(); return Choices.Count > 0 ? Choices.Dequeue() : throw new InvalidOperationException("测试未预设关闭选择"); }
        public IReadOnlyList<string> ShowOpenDialog(TrackFileFormat format) => [];
        public string? ShowSaveDialog(TrackFileFormat format, string name) => null;
        public IReadOnlyList<string> ShowOpenProjectDialog() => [];
        public string? ShowSaveProjectDialog(string name) => SavePath;
        public NewProjectRequest? ShowNewProjectDialog() => null;
        public bool ConfirmRemoveMissingRecent(string path) => false;
        public RecoveryChoice ConfirmRecovery(AutoSaveRecoveryCandidate candidate) => RecoveryChoice.Ignore;
        public void ShowInformation(string title, string message) { }
        public void ShowWarning(string title, string message) { }
        public void ShowError(string title, string message) { Errors++; }
    }

    private sealed class LifecycleAutoSave(IAutoSaveService inner) : IAutoSaveService
    {
        public int Calls { get; private set; }
        public TimeSpan Delay { get; set; }
        public TimeSpan Interval => inner.Interval;
        public async Task<bool> SaveIfDirtyAsync(HikingProject? project, string? path, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
            return await inner.SaveIfDirtyAsync(project, path, cancellationToken);
        }
        public Task DeleteAsync(Guid id, CancellationToken token = default) => inner.DeleteAsync(id, token);
        public Task DeleteForPathAsync(Guid id, string? path, CancellationToken token = default) => inner.DeleteForPathAsync(id, path, token);
        public Task<IReadOnlyList<AutoSaveRecoveryCandidate>> FindRecoveryCandidatesAsync(bool clean, CancellationToken token = default) => inner.FindRecoveryCandidatesAsync(clean, token);
        public Task<HikingProject> RecoverAsync(AutoSaveRecoveryCandidate candidate, CancellationToken token = default) => inner.RecoverAsync(candidate, token);
    }

    private async Task<bool> RunLifecycleSmokeAsync(MainWindow window, MainViewModel main, IProjectService projects,
        LifecycleAutoSave autoSave, LifecycleDialogs dialogs, string output)
    {
        var checks = new Dictionary<string, bool>(); var timings = new Dictionary<string, double>(); string? error = null;
        var folder = Path.GetDirectoryName(Path.GetFullPath(output))!;
        if (!folder.StartsWith(@"F:\Codex\Temp\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("生命周期测试必须隔离");
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        void Reset()
        {
            while (projects.OpenProjects.Count > 0) projects.Close();
            main.ResumeAfterCancelledApplicationClose();
            main.RefreshWorkspaceForAutomation();
            dialogs.Prompts.Clear(); dialogs.Choices.Clear(); dialogs.Prompted = null; dialogs.SavePath = null;
        }
        HikingProject Dirty(string name)
        { var p = projects.CreateNew(name); p.Description = "人工攻略说明必须保留"; p.MarkDirty(); main.RefreshWorkspaceForAutomation(); return p; }
        try
        {
            var p = Dirty("取消关闭测试");
            main.QueueGuideAutoSave(p); autoSave.Delay = TimeSpan.FromSeconds(3);
            // 真实经过攻略防抖，模拟已开始的慢归档；关闭提示不能等待这3秒。
            for (var i=0;i<200&&autoSave.Calls==0;i++) await Task.Delay(10);
            checks["已覆盖自动恢复写入中的关闭"] = autoSave.Calls > 0;
            var clock = Stopwatch.StartNew();
            dialogs.Prompted = () => timings["CancelPromptMs"] = clock.Elapsed.TotalMilliseconds;
            dialogs.Choices.Enqueue(UnsavedChangesChoice.Cancel);
            var cancelled = !await main.CanCloseApplicationAsync();
            checks["慢自动保存不阻塞首次关闭提示"] = timings["CancelPromptMs"] < 500;
            checks["取消保留项目和人工内容"] = cancelled && p.IsDirty && p.Description == "人工攻略说明必须保留" && projects.OpenProjects.Count == 1;
            Reset(); autoSave.Delay = TimeSpan.Zero;

            p = Dirty("定时自动保存写入关闭测试"); autoSave.Delay = TimeSpan.FromSeconds(3);
            var periodicCalls = autoSave.Calls; main.StartPeriodicAutoSaveForAutomation();
            for (var i=0;i<200&&autoSave.Calls==periodicCalls;i++) await Task.Delay(10);
            dialogs.Choices.Enqueue(UnsavedChangesChoice.Discard); clock.Restart();
            checks["定时自动保存安全取消且不拖延关闭"] = autoSave.Calls > periodicCalls && await main.CanCloseApplicationAsync() && clock.ElapsedMilliseconds < 500;
            Reset(); autoSave.Delay = TimeSpan.Zero;

            p = Dirty("不保存测试"); var calls = autoSave.Calls;
            dialogs.Choices.Enqueue(UnsavedChangesChoice.Discard); clock.Restart();
            var discard = await main.CanCloseApplicationAsync(); timings["DiscardDecisionMs"] = clock.Elapsed.TotalMilliseconds;
            checks["不保存不新增完整恢复归档"] = discard && autoSave.Calls == calls;
            checks["不保存不修改内存中的人工内容"] = p.IsDirty && p.Description == "人工攻略说明必须保留";
            Reset();

            var first = Dirty("多项目一"); var second = Dirty("多项目二");
            var original = projects.CurrentProject; dialogs.Choices.Enqueue(UnsavedChangesChoice.Discard); dialogs.Choices.Enqueue(UnsavedChangesChoice.Cancel);
            checks["后续项目取消可保留整个工作区"] = !await main.CanCloseApplicationAsync() && projects.OpenProjects.Count == 2 && first.IsDirty && second.IsDirty && ReferenceEquals(original, projects.CurrentProject);
            Reset();

            p = Dirty("保存选择位置取消"); dialogs.Choices.Enqueue(UnsavedChangesChoice.Save);
            checks["取消保存位置不关闭不丢数据"] = !await main.CanCloseApplicationAsync() && p.IsDirty;
            Reset();

            p = Dirty("保存失败测试"); dialogs.SavePath = Path.Combine(folder, "不存在的目录", "项目.hikeproj"); dialogs.Choices.Enqueue(UnsavedChangesChoice.Save);
            var errors = dialogs.Errors;
            checks["保存失败保留项目并显示错误"] = !await main.CanCloseApplicationAsync() && p.IsDirty && dialogs.Errors > errors;
            Reset();

            p = Dirty("正式保存测试"); dialogs.SavePath = Path.Combine(folder, "已保存项目.hikeproj"); dialogs.Choices.Enqueue(UnsavedChangesChoice.Save);
            var saved = await main.CanCloseApplicationAsync();
            var reloaded = await new ProjectFileService().LoadAsync(dialogs.SavePath);
            checks["选择保存完成安全保存并可重新打开"] = saved && !p.IsDirty && reloaded.Description == "人工攻略说明必须保留";
            Reset();

            p = Dirty("窗口取消测试"); dialogs.Choices.Enqueue(UnsavedChangesChoice.Cancel);
            clock.Restart(); dialogs.Prompted = () => timings["ActualWindowPromptMs"] = clock.Elapsed.TotalMilliseconds;
            window.Close(); await Task.Delay(100);
            checks["窗口关闭事件快速提示且取消后保持显示"] = window.IsVisible && p.IsDirty && timings["ActualWindowPromptMs"] < 500;

            // 再次点关闭只处理一次，列宽/布局仍在确认后保存。
            dialogs.Choices.Enqueue(UnsavedChangesChoice.Discard);
            dialogs.Prompted = () => Dispatcher.BeginInvoke(new Action(() => { if (window.IsVisible) window.Close(); })); var count = dialogs.Prompts.Count;
            clock.Restart(); window.Close();
            for(var i=0;i<500&&window.IsVisible;i++) await Task.Delay(10);
            timings["ActualWindowCloseMs"] = clock.Elapsed.TotalMilliseconds;
            checks["连续点关闭不重复询问且窗口最终关闭"] = !window.IsVisible && dialogs.Prompts.Count == count + 1;
            checks["关闭后布局设置仍保留"] = GlobalSettingsService.Current.PanelLayout is not null;
        }
        catch(Exception ex) { error = ex.ToString(); }
        var passed = error is null && checks.Count > 0 && checks.Values.All(v=>v);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new{Passed=passed,Checks=checks,Timings=timings,Error=error},new JsonSerializerOptions{WriteIndented=true}));
        return passed;
    }
}
