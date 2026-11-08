using System.Windows;
using System.Windows.Controls;
using HikingGuide.App.ViewModels;

namespace HikingGuide.App;

public partial class MainWindow
{
    internal async Task<bool> RunP91TreeMouseSmokeAsync(MainViewModel model)
    {
        var tracks = model.MapContextProject?.Tracks.Take(2).ToArray(); if (tracks is null || tracks.Length != 2) return false;
        FavoritesPanel.MouseProbe.GetCursorPos(out var cursor); var foreground = FavoritesPanel.MouseProbe.GetForegroundWindow(); var oldTopmost = Topmost;
        try
        {
            Topmost = true; Activate(); FavoritesPanel.MouseProbe.SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle); UpdateLayout();
            await Task.Delay(150);
            for (var i = 0; i < 10; i++)
            {
                var track = tracks[i % 2]; var item = FavoritesPanel.Descendants<TreeViewItem>(ProjectTree).FirstOrDefault(n => (n.DataContext as NavigationNode)?.Model == track);
                if (item is null) return false;
                var text = FavoritesPanel.Descendants<TextBlock>(item).First(t => ReferenceEquals((t.DataContext as NavigationNode)?.Model, track));
                var point = text.PointToScreen(new Point(text.ActualWidth / 2, text.ActualHeight / 2)); FavoritesPanel.MouseProbe.SetCursorPos((int)point.X, (int)point.Y);
                FavoritesPanel.MouseProbe.mouse_event(2, 0, 0, 0, UIntPtr.Zero); FavoritesPanel.MouseProbe.mouse_event(4, 0, 0, 0, UIntPtr.Zero);
                for (var wait = 0; wait < 20 && (model.PrimarySelectedTrack != track || !FavoritesPanel.Descendants<TextBlock>(this).Any(t => t.Text == "当前选中轨迹：" + track.Name)); wait++) await Task.Delay(25);
                if (model.PrimarySelectedTrack != track || !FavoritesPanel.Descendants<TextBlock>(this).Any(t => t.Text == "当前选中轨迹：" + track.Name)) return false;
            }
            return true;
        }
        finally { Topmost = oldTopmost; FavoritesPanel.MouseProbe.SetCursorPos(cursor.X, cursor.Y); if (foreground != IntPtr.Zero) FavoritesPanel.MouseProbe.SetForegroundWindow(foreground); }
    }
}
