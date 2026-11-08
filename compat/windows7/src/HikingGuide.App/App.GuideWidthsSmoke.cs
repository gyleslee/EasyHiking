using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using HikingGuide.App.ViewModels;
using HikingGuide.Application.Guides;
using HikingGuide.Core.Guides;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Projects;
using Track = HikingGuide.Core.Models.Track;

namespace HikingGuide.App;

public partial class App
{
    private async Task<bool> RunGuideWidthsSmokeAsync(MainWindow window, MainViewModel main, string output, bool reopen)
    {
        var checks = new Dictionary<string, bool>(); string? error = null;
        FavoritesPanel.MouseProbe.GetCursorPos(out var cursor); var foreground = FavoritesPanel.MouseProbe.GetForegroundWindow();
        var opened = new List<(HikingProject Project, string Path)>();
        try
        {
            if (!Favorites.Paths.FavoritesRoot.StartsWith(@"F:\Codex\Temp\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("列宽测试必须隔离用户数据。");
            var folder = Path.GetDirectoryName(Path.GetFullPath(output))!; Directory.CreateDirectory(folder);
            window.Width = 1550; window.Height = 1000; window.BottomPanelRow.Height = new GridLength(450);
            var service = new ProjectFileService();
            async Task OpenNew(string name, bool thirdPlan)
            {
                var track = new Track { Name = name, Segments = [new() { Points = [
                    new() { Latitude = 23, Longitude = 113, SourceElevation = 100 },
                    new() { Latitude = 23.001, Longitude = 113, SourceElevation = 110 },
                    new() { Latitude = 23.002, Longitude = 113, SourceElevation = 105 }] }] };
                track.RecalculateDistances(); var project = new HikingProject { Name = name }; project.AddTrack(track); project.MainGuideTrackId = track.Id;
                var structure = new GuideStructureService(); foreach (var (point, i) in track.Segments[0].Points.Select((p, i) => (p, i)))
                { var node = new GuideNode { Name = "节点" + (i + 1), TrackId = track.Id, OriginalCoordinate = new(point.Latitude, point.Longitude) }; structure.SnapNode(node, track); project.GuideNodes.Add(node); }
                GuideStructureService.ReplaceSegments(project, structure.RebuildSegments(project)); project.GuideData.Settings = project.GuideData.Settings with { ElevationSourceKind = GuideElevationSourceKind.Gps };
                if (thirdPlan) { project.GuideData.Plans.Add(new() { Order = 2 }); GuideDataReconciler.AddCondition(project.GuideData.Settings, "摄影点"); }
                var path = Path.Combine(folder, name + ".hikeproj"); await service.SaveAsync(project, path, true);
                await main.OpenForAutomationAsync(path); window.GuideWorkbench.SelectProject(main.MapContextProject); await window.GuideWorkbench.CalculateAsync();
                await Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
                opened.Add((main.MapContextProject!, path)); await service.SaveAsync(main.MapContextProject!, path, true);
            }
            await OpenNew(reopen ? "重启后新建项目" : "初始项目", !reopen);
            var table = window.GuideWorkbench.Table;
            var keys = new[] { "Guide:GuideNodeName", "Guide:Plan:行进方案1", "Guide:Arrival:方案1计划抵达时间", "Guide:Location:营地" };
            DataGridColumn Column(string key) => table.Columns.First(c =>
                GuideTablePresentation.ColumnWidthKey(new(c.SortMemberPath, c.Header.ToString()!, "", Location: c.SortMemberPath.StartsWith("Location:"))) == key);
            bool Matches(string key) => GlobalSettingsService.Current.GuideTableColumnWidths.TryGetValue(key, out var width) && Math.Abs(Column(key).ActualWidth - width) < 2;
            async Task Drag(DataGrid grid, DataGridColumn column)
            {
                grid.ScrollIntoView(grid.Items[0], column); await Dispatcher.InvokeAsync(grid.UpdateLayout, DispatcherPriority.Loaded);
                await Dispatcher.InvokeAsync(grid.UpdateLayout, DispatcherPriority.Render);
                var header = FavoritesPanel.Descendants<DataGridColumnHeader>(grid).First(h => ReferenceEquals(h.Column, column));
                var grip = header.Template.FindName("PART_RightHeaderGripper", header) as Thumb;
                var point = grip is { ActualWidth: > 0, ActualHeight: > 0 }
                    ? grip.PointToScreen(new Point(grip.ActualWidth / 2, grip.ActualHeight / 2))
                    : header.PointToScreen(new Point(header.ActualWidth - 2, header.ActualHeight / 2));
                FavoritesPanel.MouseProbe.SetCursorPos((int)point.X, (int)point.Y); await Task.Delay(100);
                FavoritesPanel.MouseProbe.mouse_event(2, 0, 0, 0, UIntPtr.Zero); await Task.Delay(70);
                for (var i = 1; i <= 5; i++) { FavoritesPanel.MouseProbe.SetCursorPos((int)point.X + i * 14, (int)point.Y); await Task.Delay(70); }
                FavoritesPanel.MouseProbe.mouse_event(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(100);
                await Dispatcher.InvokeAsync(grid.UpdateLayout, DispatcherPriority.Render);
            }
            if (reopen)
            {
                foreach (var key in keys) checks["新进程新项目恢复:" + key] = Matches(key);
                var day = window.GuideWorkbench.DailyTable.Columns[1];
                checks["重启恢复每日表列宽"] = GlobalSettingsService.Current.GuideTableColumnWidths.TryGetValue("Daily:Start", out var width) && Math.Abs(day.ActualWidth - width) < 2;
                foreach (var column in window.GuideWorkbench.DailyTable.Columns)
                    if (GlobalSettingsService.Current.GuideTableColumnWidths.TryGetValue("Daily:" + column.SortMemberPath, out var savedWidth))
                        checks["新项目恢复每日列:" + column.SortMemberPath] = Math.Abs(column.ActualWidth - savedWidth) < 2;
            }
            else
            {
                window.Topmost = true; window.Activate(); FavoritesPanel.MouseProbe.SetForegroundWindow(new WindowInteropHelper(window).Handle); await Task.Delay(250);
                foreach (var key in keys)
                {
                    var column = Column(key); var old = column.ActualWidth; var clean = main.MapContextProject!.IsDirty;
                    await Drag(table, column);
                    checks["实体鼠标改变列宽:" + key] = column.ActualWidth > old + 20 && Matches(key);
                    checks["列宽不改变项目脏状态:" + key] = main.MapContextProject.IsDirty == clean;
                }
                async Task DoubleFit(DataGrid grid, DataGridColumn column)
                {
                    column.Width = 400; grid.ScrollIntoView(grid.Items[0], column); await Dispatcher.InvokeAsync(grid.UpdateLayout, DispatcherPriority.Render);
                    var header = FavoritesPanel.Descendants<DataGridColumnHeader>(grid).First(h => ReferenceEquals(h.Column, column));
                    var grip = (Thumb)header.Template.FindName("PART_RightHeaderGripper", header);
                    var p = grip.PointToScreen(new Point(grip.ActualWidth / 2, grip.ActualHeight / 2));
                    FavoritesPanel.MouseProbe.SetCursorPos((int)p.X, (int)p.Y); await Task.Delay(100);
                    for (var i = 0; i < 2; i++) { FavoritesPanel.MouseProbe.mouse_event(2, 0, 0, 0, UIntPtr.Zero); FavoritesPanel.MouseProbe.mouse_event(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(70); }
                    await Task.Delay(250); await Dispatcher.InvokeAsync(grid.UpdateLayout, DispatcherPriority.ContextIdle);
                }
                var nameColumn = Column("Guide:GuideNodeName"); await DoubleFit(table, nameColumn);
                checks["上表实体双击自动适配并保存实际宽度"] = nameColumn.ActualWidth < 390 && nameColumn.Width.IsAbsolute && Matches("Guide:GuideNodeName");
                var dailyName = window.GuideWorkbench.DailyTable.Columns[1]; await DoubleFit(window.GuideWorkbench.DailyTable, dailyName);
                checks["每日表实体双击自动适配并保存实际宽度"] = dailyName.ActualWidth < 390 && dailyName.Width.IsAbsolute && GlobalSettingsService.Current.GuideTableColumnWidths.TryGetValue("Daily:Start", out var fitted) && Math.Abs(fitted - dailyName.ActualWidth) < 2;
                await Task.Delay(800); await window.GuideWorkbench.FlushColumnWidthsAsync();
                using (var saved = JsonDocument.Parse(await File.ReadAllTextAsync(GlobalSettingsService.SettingsFilePath)))
                    checks["自动保存到软件设置"] = saved.RootElement.GetProperty("guideTableColumnWidths").EnumerateObject().Count() >= keys.Length;
                await OpenNew("不同方案数量的新项目", false);
                checks["新项目每日表沿用双击列宽"] = Math.Abs(window.GuideWorkbench.DailyTable.Columns[1].ActualWidth - GlobalSettingsService.Current.GuideTableColumnWidths["Daily:Start"]) < 2;
                foreach (var key in keys) checks["不同项目沿用列宽:" + key] = Matches(key);
                await main.OpenForAutomationAsync(opened[0].Path); await window.GuideWorkbench.CalculateAsync(); await Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
                foreach (var key in keys) checks["切回原项目列宽不变:" + key] = Matches(key);
                foreach (var (project, path) in opened) await service.SaveAsync(project, path, true);
                // 重开已打开路径可能得到新实例，保存当前活跃的实例，避免验收停在人工确认框。
                await service.SaveAsync(main.MapContextProject!, opened[0].Path, true);
                await main.FlushGuideAutoSaveAsync();
                // 最后一次宽度调整不等待去抖计时器：正常关闭必须Flush。
                var days = window.GuideWorkbench.DailyTable; var day = days.Columns[1]; var before = day.ActualWidth;
                await Drag(days, day); checks["每日表也可调整且立即更新全局值"] = day.ActualWidth > before + 20 && GlobalSettingsService.Current.GuideTableColumnWidths.ContainsKey("Daily:Start");
                window.Topmost = false;
            }
            checks["状态提示没有连续句号分号"] = !Regex.IsMatch(window.GuideWorkbench.Status, "[。；;·]\\s*[。；;·]") && !window.GuideWorkbench.Status.EndsWith('；');
            CaptureWindowPng(window, Path.ChangeExtension(output, ".png"));
        }
        catch (Exception exception) { error = exception.ToString(); }
        finally { FavoritesPanel.MouseProbe.mouse_event(4, 0, 0, 0, UIntPtr.Zero); FavoritesPanel.MouseProbe.SetCursorPos(cursor.X, cursor.Y); if (foreground != IntPtr.Zero) FavoritesPanel.MouseProbe.SetForegroundWindow(foreground); }
        var success = error is null && checks.Count > 0 && checks.Values.All(v => v);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { Success = success, Checks = checks, Error = error, Widths = GlobalSettingsService.Current.GuideTableColumnWidths }, new JsonSerializerOptions { WriteIndented = true }));
        return success;
    }
}
