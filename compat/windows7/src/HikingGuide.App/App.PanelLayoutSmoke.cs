using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace HikingGuide.App;

public partial class App
{
    private async Task<bool> RunPanelLayoutSmokeAsync(MainWindow window, string output, bool reopen)
    {
        var checks = new Dictionary<string, bool>(); string? error = null;
        FavoritesPanel.MouseProbe.GetCursorPos(out var cursor);
        var foreground = FavoritesPanel.MouseProbe.GetForegroundWindow();
        try
        {
            if (!Favorites.Paths.FavoritesRoot.StartsWith(@"F:\Codex\Temp\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("界面布局测试必须使用隔离数据目录。");
            await Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
            var initial = window.CapturePanelLayout();
            if (reopen)
            {
                var saved = GlobalSettingsService.Current.PanelLayout;
                checks["重启恢复上下比例"] = saved is not null && initial is not null && Math.Abs(saved.BottomFraction - initial.BottomFraction) < .003;
                checks["重启恢复项目面板宽度"] = saved is not null && initial is not null && Math.Abs(saved.ProjectFraction - initial.ProjectFraction) < .003;
                checks["重启恢复属性面板宽度"] = saved is not null && initial is not null && Math.Abs(saved.PropertiesFraction - initial.PropertiesFraction) < .003;
                window.Height = 650; await Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                var rows = ((Grid)window.FindName("WindowPanels")).RowDefinitions;
                checks["缩小窗口仍有可见上下区域"] = rows[1].ActualHeight >= 120 && rows[3].ActualHeight >= 200;
            }
            else
            {
                window.Topmost = true; window.Activate();
                FavoritesPanel.MouseProbe.SetForegroundWindow(new WindowInteropHelper(window).Handle);
                await Task.Delay(300);
                async Task Drag(string name, int dx, int dy)
                {
                    var splitter = (FrameworkElement)window.FindName(name);
                    var point = splitter.PointToScreen(new Point(splitter.ActualWidth / 2, splitter.ActualHeight / 2));
                    FavoritesPanel.MouseProbe.SetCursorPos((int)point.X, (int)point.Y); await Task.Delay(100);
                    FavoritesPanel.MouseProbe.mouse_event(2, 0, 0, 0, UIntPtr.Zero); await Task.Delay(70);
                    for (var step = 1; step <= 5; step++)
                    { FavoritesPanel.MouseProbe.SetCursorPos((int)point.X + dx * step / 5, (int)point.Y + dy * step / 5); await Task.Delay(70); }
                    FavoritesPanel.MouseProbe.mouse_event(4, 0, 0, 0, UIntPtr.Zero);
                    await Task.Delay(200); await window.PanelLayoutSaveTask;
                    await Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
                }
                await Drag("UpperBottomSplitter", 0, -120);
                var vertical = window.CapturePanelLayout();
                checks["实体鼠标调整上下区域"] = vertical is not null && initial is not null && vertical.BottomFraction > initial.BottomFraction + .08;
                checks["松开立即保存上下比例"] = vertical is not null && GlobalSettingsService.Current.PanelLayout is { } saved && Math.Abs(saved.BottomFraction - vertical.BottomFraction) < .003 && File.Exists(GlobalSettingsService.SettingsFilePath);
                await Drag("ProjectPanelSplitter", 65, 0);
                var left = window.CapturePanelLayout();
                checks["上方项目分隔条仍可拖动"] = left is not null && vertical is not null && left.ProjectFraction > vertical.ProjectFraction + .02;
                await Drag("PropertiesPanelSplitter", -60, 0);
                var right = window.CapturePanelLayout();
                checks["上方属性分隔条仍可拖动"] = right is not null && left is not null && right.PropertiesFraction > left.PropertiesFraction + .02;
                checks["三方向布局均持久化"] = right is not null && GlobalSettingsService.Current.PanelLayout is { } layout && Math.Abs(layout.PropertiesFraction - right.PropertiesFraction) < .003;
                window.Topmost = false;
            }
        }
        catch (Exception exception) { error = exception.ToString(); }
        finally
        {
            FavoritesPanel.MouseProbe.mouse_event(4, 0, 0, 0, UIntPtr.Zero);
            FavoritesPanel.MouseProbe.SetCursorPos(cursor.X, cursor.Y);
            if (foreground != IntPtr.Zero) FavoritesPanel.MouseProbe.SetForegroundWindow(foreground);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var success = error is null && checks.Count > 0 && checks.Values.All(v => v);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { Success = success, Checks = checks, Error = error,
            Layout = window.CapturePanelLayout() }, new JsonSerializerOptions { WriteIndented = true }));
        return success;
    }
}
