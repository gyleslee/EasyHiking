using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using HikingGuide.App.ViewModels;
using HikingGuide.Core.Guides;
using HikingGuide.Infrastructure.Projects;

namespace HikingGuide.App;

public partial class App
{
    private async Task<bool> RunGuideBoundarySmokeAsync(MainWindow window, MainViewModel main, string output, string source)
    {
        var checks = new Dictionary<string, bool>(); string? error = null;
        try
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(output))!;
            if (!Favorites.Paths.FavoritesRoot.StartsWith(@"F:\Codex\Temp\", StringComparison.OrdinalIgnoreCase)
                || !folder.StartsWith(@"F:\Codex\Temp\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("每日边界回归必须隔离用户数据。");
            Directory.CreateDirectory(folder);
            var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
            var copy = Path.Combine(folder, "333失效边界验收副本.hikeproj"); File.Copy(source, copy);
            await main.OpenForAutomationAsync(copy); var project = main.MapContextProject!; var workbench = window.GuideWorkbench;
            workbench.SelectProject(project); await Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
            if (workbench.CalculationTask is { } task) await task;
            window.Width = 1600; window.Height = 1000; window.BottomPanelRow.Height = new GridLength(500);
            var nodes = GuideDataReconciler.OrderedNodes(project).ToArray(); var plans = project.GuideData.Plans.OrderBy(p => p.Order).ToArray();
            var manual = JsonSerializer.Serialize(project.GuideData.Manual); var notes = plans.ToDictionary(p => p.PlanId, p => JsonSerializer.Serialize(p.DailyNotes));
            checks["真实333副本重现两方案失效边界"] = nodes.Length == 51 && plans.Length == 2 && plans.All(p => p.NeedsReview && p.DailyStartNodeIds.Count == 8);
            string Cell(int row, Guid planId) => workbench.Table.Items.OfType<GuideWorkbenchControl.Row>().ElementAt(row)["Arrival:" + planId];
            checks["未确认前抵达时间保持留空"] = plans.All(p => Cell(1, p.PlanId) == "");
            checks["明确提示方案原因与直接确认入口"] = workbench.Status.Contains("行进方案1有1个每日起点") && workbench.Status.Contains("计划抵达时间暂停计算") && workbench.DailyBoundaryConfirmationButton.IsEnabled;
            await workbench.EditDepartureForAutomationAsync(0, "06:15"); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            checks["选择出发时间确实保存但不会绕过边界确认"] = plans[0].DepartureTimes[nodes[0].Id] == new TimeOnly(6, 15) && Cell(1, plans[0].PlanId) == "";
            var selectedTimes = plans.ToDictionary(p => p.PlanId, p => JsonSerializer.Serialize(p.DepartureTimes));
            await File.WriteAllTextAsync(Path.Combine(folder, "boundary-action.txt"), "cancel");
            workbench.DailyBoundaryConfirmationButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            checks["取消确认不改变边界或人工数据"] = plans[0].NeedsReview && plans[0].DailyStartNodeIds.Count == 8 && JsonSerializer.Serialize(project.GuideData.Manual) == manual;
            foreach (var plan in plans)
            {
                workbench.SelectPlanForAutomation(plan.PlanId);
                await File.WriteAllTextAsync(Path.Combine(folder, "boundary-action.txt"), "confirm");
                workbench.DailyBoundaryConfirmationButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
                checks["方案确认后恢复七天:" + plan.Order] = !plan.NeedsReview && plan.DailyStartNodeIds.Count == 7;
                checks["方案确认后起点及后续抵达自动显示:" + plan.Order] = Cell(0, plan.PlanId) == plan.DepartureTimes[nodes[0].Id]!.Value.ToString("HH:mm") && Cell(1, plan.PlanId) != "";
                var boundary = nodes.Select((n, i) => (n, i)).First(x => x.i > 0 && plan.DailyStartNodeIds.Contains(x.n.Id));
                checks["方案日界两行抵达出发:" + plan.Order] = Cell(boundary.i, plan.PlanId).Contains("D1抵达") && Cell(boundary.i, plan.PlanId).Contains("D2出发");
                checks["无需高程重算即可恢复时间且确认按钮停用:" + plan.Order] = !workbench.DailyBoundaryConfirmationButton.IsEnabled;
            }
            checks["第一方案真实人工计划用时联动"] = Cell(1, plans[0].PlanId) == "07:03";
            checks["第二方案独立人工计划用时联动"] = Cell(1, plans[1].PlanId) == "07:48";
            checks["人工分段节点原文与全部备注出发时间保留"] = JsonSerializer.Serialize(project.GuideData.Manual) == manual
                && plans.All(p => notes[p.PlanId] == JsonSerializer.Serialize(p.DailyNotes) && selectedTimes[p.PlanId] == JsonSerializer.Serialize(p.DepartureTimes));
            var service = new ProjectFileService(); await service.SaveAsync(project, copy, true);
            var reload = await service.LoadAsync(copy); var snapshot = GuideExportSnapshotBuilder.Build(reload);
            checks["保存重新读取不会再次进入失效状态"] = reload.GuideData.Plans.All(p => !p.NeedsReview && p.DailyStartNodeIds.Count == 7)
                && snapshot.PlanTimings.Values.All(t => t.Arrivals[nodes[1].Id] is not null);
            var finalSourceHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
            checks["真实原项目与备份未被验收修改"] = sourceHash.SequenceEqual(finalSourceHash);
            CaptureWindowPng(window, Path.ChangeExtension(output, ".png"));
        }
        catch (Exception exception) { error = exception.ToString(); }
        var success = error is null && checks.Count > 0 && checks.Values.All(v => v);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { Success = success, Checks = checks, Error = error }, new JsonSerializerOptions { WriteIndented = true }));
        return success;
    }
}
