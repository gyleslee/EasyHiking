using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using HikingGuide.App.ViewModels;
using HikingGuide.Application.Guides;
using HikingGuide.Core.Guides;
using HikingGuide.Core.Models;
using HikingGuide.Infrastructure.Guides;
using HikingGuide.Infrastructure.Projects;
using Track = HikingGuide.Core.Models.Track;

namespace HikingGuide.App;

public partial class App
{
    private async Task<bool> RunP101SmokeAsync(MainWindow window, MainViewModel main, string output)
    {
        var checks = new Dictionary<string, bool>(); var diagnostics = new List<string>(); string? error = null;
        FavoritesPanel.MouseProbe.GetCursorPos(out var cursor); var foreground = FavoritesPanel.MouseProbe.GetForegroundWindow();
        try
        {
            if (!Favorites.Paths.FavoritesRoot.StartsWith(@"F:\Codex\Temp\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("P10-1测试必须隔离用户数据。");
            var folder = Path.GetDirectoryName(Path.GetFullPath(output))!; Directory.CreateDirectory(folder);
            window.Width = 1600; window.Height = 1000; window.BottomPanelRow.Height = new GridLength(500);
            var track = new Track { Name = "P10-1五节点测试", Segments = [new() { Points = Enumerable.Range(0, 5).Select(i =>
                new TrackPoint { Latitude = 23 + .001 * i, Longitude = 113, SourceElevation = 100.5 + i * 11.25 }).ToList() }] };
            track.RecalculateDistances(); var project = new HikingProject { Name = "P10-1界面验收副本" }; project.AddTrack(track); project.MainGuideTrackId = track.Id;
            var structure = new GuideStructureService();
            foreach (var (p, i) in track.Segments[0].Points.Select((p, i) => (p, i)))
            { var node = new GuideNode { Name = "节点" + (i + 1), TrackId = track.Id, OriginalCoordinate = p.Coordinate }; structure.SnapNode(node, track); project.GuideNodes.Add(node); }
            GuideStructureService.ReplaceSegments(project, structure.RebuildSegments(project));
            project.GuideData.Settings = project.GuideData.Settings with { ElevationSourceKind = GuideElevationSourceKind.Gps };
            var ids = project.GuideNodes.Select(n => n.Id).ToArray(); GuideDataReconciler.Reconcile(project.GuideData, track.Id, ids);
            for (var i = 1; i < ids.Length; i++) { var key = new GuideSegmentKey(track.Id, ids[i - 1], ids[i]); project.GuideData.Manual.Segments[key.StorageKey] = new() { SegmentKey = key, PlannedDurationHours = 1.25, RoadCondition = "普通步道" }; }
            var service = new ProjectFileService(); var path = Path.Combine(folder, "P101界面.hikeproj"); await service.SaveAsync(project, path, true);
            await main.OpenForAutomationAsync(path); project = main.MapContextProject!; var workbench = window.GuideWorkbench;
            workbench.SelectProject(project); await workbench.CalculateAsync(); await Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            window.Topmost = true; window.Activate(); FavoritesPanel.MouseProbe.SetForegroundWindow(new WindowInteropHelper(window).Handle); await Task.Delay(200);
            var table = workbench.Table; var days = workbench.DailyTable; var plan = project.GuideData.Plans[0];
            checks["缓坡陡坡35百分比分界提示"] = FavoritesPanel.Descendants<TextBox>(workbench).Any(b => b.ToolTip is string text && text.Contains("缓坡：坡度大于2.1%且小于35%"))
                && FavoritesPanel.Descendants<TextBox>(workbench).Any(b => b.ToolTip is string text && text.Contains("陡坡：坡度大于或等于35%"));
            GuideWorkbenchControl.Row Row(int index) => table.Items.OfType<GuideWorkbenchControl.Row>().ElementAt(index);
            async Task<FrameworkElement> Content(DataGrid grid, object row, DataGridColumn column)
            {
                grid.ScrollIntoView(row, column); grid.SelectedItem = row; grid.CurrentCell = new(row, column);
                await Dispatcher.InvokeAsync(grid.UpdateLayout, DispatcherPriority.Loaded);
                var content = column.GetCellContent(row) ?? throw new InvalidOperationException("测试单元格未生成。");
                if (content is ContentPresenter && VisualTreeHelper.GetChildrenCount(content) > 0)
                    content = (FrameworkElement)VisualTreeHelper.GetChild(content, 0);
                return content;
            }
            var planColumn = table.Columns.First(c => c.SortMemberPath == "Plan:" + plan.PlanId);
            var button = (Button)await Content(table, Row(2), planColumn); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.InvokeAsync(table.UpdateLayout, DispatcherPriority.Background);
            checks["点击设置两天边界"] = plan.DailyStartNodeIds.Contains(ids[2]) && days.Items.Count == 2;
            button = (Button)await Content(table, Row(2), planColumn);
            checks["格内仅显示起终点"] = button.Content is TextBlock label && label.Text == "D1终点\nD2起点" && !label.Text.Contains("距离");
            checks["悬浮含每天总结且没有设置提醒"] = button.ToolTip is ToolTip tooltip && tooltip.Content is TextBlock tipText && tipText.Text.Contains("D1距离") && tipText.Text.Contains("D2距离") && !tipText.Text.Contains("点击设置");
            var point = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
            FavoritesPanel.MouseProbe.SetCursorPos((int)point.X, (int)point.Y); await Task.Delay(1100);
            checks["实体鼠标悬浮显示"] = button.ToolTip is ToolTip { IsOpen: true };
            FavoritesPanel.MouseProbe.SetCursorPos((int)point.X, (int)point.Y - 80); await Task.Delay(120);
            checks["鼠标离开自动隐藏"] = button.ToolTip is ToolTip { IsOpen: false };
            checks["首行路况可以输入"] = await workbench.EditCellForAutomationAsync(0, "RoadCondition", "起点水泥路\n注意路口");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            checks["首行路况模型独立保存"] = project.GuideData.Manual.Nodes[ids[0]].StartRoadCondition == "起点水泥路\n注意路口";
            async Task ChooseTime(int rowIndex, int hour, int minute)
            {
                var pickerColumn = days.Columns.First(c => c.SortMemberPath == "Departure");
                var picker = (GuideTimePicker)await Content(days, days.Items[rowIndex], pickerColumn);
                picker.Open(); await Dispatcher.InvokeAsync(picker.PickerPanel.UpdateLayout, DispatcherPriority.Loaded);
                picker.Hours.SelectedIndex = hour; picker.Minutes.SelectedIndex = minute;
                await Task.Delay(180);
                await Dispatcher.InvokeAsync(picker.PickerPanel.UpdateLayout, DispatcherPriority.ContextIdle);
                await Dispatcher.InvokeAsync(picker.PickerPanel.UpdateLayout, DispatcherPriority.Render);
                foreach (var list in new[] { picker.Hours, picker.Minutes })
                {
                    var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(list.SelectedIndex);
                    var center = item.TranslatePoint(new Point(0, item.ActualHeight / 2), list).Y;
                    diagnostics.Add($"滚动时间选中项:{list.SelectedItem};中心:{center:F1};列表高度:{list.ActualHeight:F1}");
                    checks["小时分钟选中项保持滚动区域中央"] = checks.GetValueOrDefault("小时分钟选中项保持滚动区域中央", true) && Math.Abs(center - list.ActualHeight / 2) < 3;
                }
                if (rowIndex == 0)
                {
                    var panel = picker.PickerPanel;
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth), (int)Math.Ceiling(panel.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(panel);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(folder, "时间选择器.png")); encoder.Save(file);
                }
                var p = picker.ConfirmButton.PointToScreen(new Point(picker.ConfirmButton.ActualWidth / 2, picker.ConfirmButton.ActualHeight / 2));
                FavoritesPanel.MouseProbe.SetCursorPos((int)p.X, (int)p.Y); FavoritesPanel.MouseProbe.mouse_event(2, 0, 0, 0, UIntPtr.Zero); FavoritesPanel.MouseProbe.mouse_event(4, 0, 0, 0, UIntPtr.Zero);
                await Task.Delay(160);
            }
            await ChooseTime(0, 6, 37); checks["实体点击时间选择器确定后立即提交"] = plan.DepartureTimes[ids[0]] == new TimeOnly(6, 37);
            await Task.Delay(150);
            checks["当天起点自动显示出发时间"] = Row(0)["Arrival:" + plan.PlanId] == "06:37";
            checks["无需点击更新抵达时间"] = Row(1)["Arrival:" + plan.PlanId] == "07:52" && Row(2)["Arrival:" + plan.PlanId] == "09:07";
            await ChooseTime(1, 23, 30); checks["第二天时间选择器确定"] = plan.DepartureTimes[ids[2]] == new TimeOnly(23, 30); await Task.Delay(100);
            checks["日界两行分别保留抵达与出发"] = Row(2)["Arrival:" + plan.PlanId] == "D1抵达 09:07\nD2出发 23:30";
            checks["跨午夜独立更新"] = Row(3)["Arrival:" + plan.PlanId] == "00:45";
            var dep = days.Columns.First(c => c.SortMemberPath == "Departure"); var selector = (GuideTimePicker)await Content(days, days.Items[0], dep);
            checks["小时分钟滚动选择范围"] = selector.Hours.Items.Count == 24 && selector.Minutes.Items.Count == 60;
            selector.Open(); selector.Hours.SelectedIndex = 15; selector.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            checks["取消时间不修改原值"] = plan.DepartureTimes[ids[0]] == new TimeOnly(6, 37);
            selector.Open(); selector.ClearButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(120);
            checks["清空出发时间同步清空起点及当天后续时间"] = Row(0)["Arrival:" + plan.PlanId] == "" && Row(1)["Arrival:" + plan.PlanId] == "";
            await ChooseTime(0, 6, 37);
            // 用实体方向键离开输入框，再直接键入新目标；不依赖双击。
            await workbench.EditCellForAutomationAsync(1, "PlannedDuration", "1.50"); await Task.Delay(100);
            var duration = table.Columns.First(c => c.SortMemberPath == "PlannedDuration");
            var content = await Content(table, Row(1), duration); DependencyObject? ancestor = content;
            while (ancestor is not null && ancestor is not DataGridCell) ancestor = VisualTreeHelper.GetParent(ancestor);
            ((DataGridCell)ancestor!).Focus(); table.BeginEdit(); await Dispatcher.InvokeAsync(table.UpdateLayout, DispatcherPriority.Loaded);
            window.Activate(); FavoritesPanel.MouseProbe.SetForegroundWindow(new WindowInteropHelper(window).Handle);
            var keyboardEditor = (TextBox)duration.GetCellContent(Row(1)); keyboardEditor.Focus();
            var editorPoint = keyboardEditor.PointToScreen(new Point(keyboardEditor.ActualWidth / 2, keyboardEditor.ActualHeight / 2));
            FavoritesPanel.MouseProbe.SetCursorPos((int)editorPoint.X, (int)editorPoint.Y);
            FavoritesPanel.MouseProbe.mouse_event(2, 0, 0, 0, UIntPtr.Zero); FavoritesPanel.MouseProbe.mouse_event(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(100);
            diagnostics.Add("按键前:" + Keyboard.FocusedElement?.GetType().Name + ";行:" + table.Items.IndexOf(table.CurrentItem) + ";修饰键:" + Keyboard.Modifiers + ";测试窗口在前台:" + (FavoritesPanel.MouseProbe.GetForegroundWindow() == new WindowInteropHelper(window).Handle));
            FavoritesPanel.MouseProbe.keybd_event(0x28, 0, 0, UIntPtr.Zero); FavoritesPanel.MouseProbe.keybd_event(0x28, 0, 2, UIntPtr.Zero); await Task.Delay(200);
            checks["方向键移到下一行且自动进入编辑"] = table.CurrentItem is GuideWorkbenchControl.Row current && current.Id == ids[2] && Keyboard.FocusedElement is TextBox;
            diagnostics.Add(workbench.KeyboardNavigationTrace + ";实际行:" + table.Items.IndexOf(table.CurrentItem));
            if (Keyboard.FocusedElement is TextBox editor) { editor.Text = "2.75"; }
            FavoritesPanel.MouseProbe.keybd_event(0x27, 0, 0, UIntPtr.Zero); FavoritesPanel.MouseProbe.keybd_event(0x27, 0, 2, UIntPtr.Zero); await Task.Delay(200);
            checks["方向键提交人工计划用时"] = project.GuideData.Manual.Segments[new GuideSegmentKey(track.Id, ids[1], ids[2]).StorageKey].PlannedDurationHours == 2.75;
            diagnostics.Add(workbench.KeyboardNavigationTrace + ";用时:" + project.GuideData.Manual.Segments[new GuideSegmentKey(track.Id, ids[1], ids[2]).StorageKey].PlannedDurationHours);
            var dayRow = days.Items.OfType<GuideWorkbenchControl.DayRow>().First(); var notesCol = days.Columns.First(c => c.SortMemberPath == "Notes");
            content = await Content(days, dayRow, notesCol); ancestor = content; while (ancestor is not null && ancestor is not DataGridCell) ancestor = VisualTreeHelper.GetParent(ancestor);
            ((DataGridCell)ancestor!).Focus(); days.BeginEdit(); await Dispatcher.InvokeAsync(days.UpdateLayout, DispatcherPriority.Loaded);
            var notesEditor = notesCol.GetCellContent(dayRow) as TextBox;
            checks["末列备注无限制并可多行输入"] = notesEditor is { MaxLength: 0, AcceptsReturn: true } && (string)notesCol.Header == "备注";
            if (notesEditor is not null) { notesEditor.Text = "第一天备注\n第二行"; notesEditor.GetBindingExpression(TextBox.TextProperty)?.UpdateSource(); }
            workbench.CommitEdits();
            checks["备注保存到当前方案当天"] = plan.DailyNotes.GetValueOrDefault(ids[0]) == "第一天备注\n第二行";
            checks["保留计划总用时列且一位小数"] = days.Columns.Any(c => (string)c.Header == "计划总用时h") && dayRow.Duration == "4.3";
            var indexText = (TextBlock)await Content(table, Row(0), table.Columns[0]);
            checks["编号水平垂直居中"] = indexText.TextAlignment == TextAlignment.Center && indexText.VerticalAlignment == VerticalAlignment.Center;
            checks["海拔整数距离一位小数"] = !Row(0)["Elevation"].Contains('.') && Row(1)["DistanceFromStart"] == "0.1";
            checks["每日距离与时长一位小数高程整数"] = !dayRow.Ascent.Contains('.') && dayRow.Distance == "0.2" && dayRow.Duration.Contains('.');
            foreach (var (name, width) in new[] { ("Start", 135d), ("Departure", 115d), ("Notes", 310d) }) days.Columns.First(c => c.SortMemberPath == name).Width = width;
            await workbench.FlushColumnWidthsAsync();
            checks["每日所有列按语义全局保存"] = GlobalSettingsService.Current.GuideTableColumnWidths["Daily:Notes"] == 310 && GlobalSettingsService.Current.GuideTableColumnWidths["Daily:Departure"] == 115;
            var propertyName = FavoritesPanel.Descendants<TextBlock>(window).FirstOrDefault(t => t.Text == "所属轨迹");
            checks["属性名称六字符宽自动换行"] = propertyName is not null && Math.Abs(propertyName.Width - propertyName.FontSize * 6) < .1 && propertyName.TextWrapping == TextWrapping.Wrap;
            await service.SaveAsync(project, path, true); var loaded = await service.LoadAsync(path);
            checks["项目重新读取保存路况备注出发时间"] = loaded.GuideData.Manual.Nodes[ids[0]].StartRoadCondition.Contains("起点水泥路") && loaded.GuideData.Plans[0].DailyNotes[ids[0]] == "第一天备注\n第二行" && loaded.GuideData.Plans[0].DepartureTimes[ids[0]] == new TimeOnly(6, 37);
            await new ExcelGuideExporter().ExportAsync(Path.Combine(AppContext.BaseDirectory, "Templates", "攻略数据模板.xlsx"), Path.Combine(folder, "P101导出.xlsx"), GuideExportSnapshotBuilder.Build(project), true);
            checks["完整Excel导出验证"] = File.Exists(Path.Combine(folder, "P101导出.xlsx"));
            main.LocateGuideNode(ids[2]); await Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            CaptureWindowPng(window, Path.ChangeExtension(output, ".png"));
            window.Topmost = false;
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally { FavoritesPanel.MouseProbe.SetCursorPos(cursor.X, cursor.Y); FavoritesPanel.MouseProbe.SetForegroundWindow(foreground); }
        var success = error is null && checks.Values.All(v => v);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { Success = success, Checks = checks, Diagnostics = diagnostics, Error = error }, new JsonSerializerOptions { WriteIndented = true }));
        return success;
    }
}
