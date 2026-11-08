using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HikingGuide.Core.Favorites;

namespace HikingGuide.App;

public sealed partial class FavoritesPanel
{
    internal async Task<Dictionary<string, object>> RunP91PanelSmokeAsync()
    {
        var result = new Dictionary<string, object>(); await RefreshAsync(); _list.UpdateLayout();
        var points = _list.Items.Cast<FavoritePoint>().ToArray();
        result["NameOrder"] = points.Select(p => p.Id).SequenceEqual(FavoriteNameOrdering.Sort(points).Select(p => p.Id));
        bool noEllipsis = true, fullNames = true, margins = true, padding = true, wrapped = false;
        foreach (var point in points)
        {
            _list.ScrollIntoView(point); _list.UpdateLayout(); await Task.Delay(25);
            if (_list.ItemContainerGenerator.ContainerFromItem(point) is not ListBoxItem item) { fullNames = false; continue; }
            var text = Descendants<TextBlock>(item).FirstOrDefault(t => t.Text == point.Name);
            var border = text is null ? null : VisualTreeHelper.GetParent(text) as Border;
            noEllipsis &= text?.TextTrimming == TextTrimming.None; fullNames &= text is not null && border is not null && text.ActualHeight + 12 <= border.ActualHeight + .5;
            margins &= item.Margin == new Thickness(4); padding &= border?.Padding == new Thickness(10, 6, 10, 6);
            if (point.Name.Length >= 100 && text is not null) wrapped |= text.ActualHeight > text.FontSize * 2;
        }
        result["NoEllipsis"] = noEllipsis; result["FullNamesVisible"] = fullNames; result["UniformMarginPadding"] = margins && padding; result["LongNameWrap"] = wrapped;
        result["LongestName"] = points.Max(p => p.Name.Length);
        var manager = new FavoritesPanel(true); var managerWindow = new Window { Owner = OwnerWindow, Title = "P9-1管理器验收", Width = 1050, Height = 650, Content = manager };
        managerWindow.Show(); await Task.Delay(150);
        var buttons = Descendants<Button>(manager).Select(b => b.Content?.ToString()).ToArray();
        result["ManagerToolbar"] = !buttons.Contains("打开收藏点管理器") && !Descendants<CheckBox>(manager).Any(c => Equals(c.Content, "地图上显示收藏点")) && Descendants<TextBlock>(manager).Any(t => t.Text == "搜索🔍") && buttons.Contains("导出为KMZ…");
        managerWindow.Close(); var owner = OwnerWindow; var oldTopmost = owner.Topmost; MouseProbe.GetCursorPos(out var original); var oldForeground = MouseProbe.GetForegroundWindow();
        try
        {
            owner.Topmost = true; owner.Activate(); MouseProbe.SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(owner).Handle); await Task.Delay(100);
            var targets = points.Where(p => p.Name.Length <= 10).Take(3).ToArray(); var maxOpen = 0; bool enters = true, leaves = true;
            for (var i = 0; i < 20; i++)
            {
                var point = targets[i % targets.Length]; _list.ScrollIntoView(point); _list.UpdateLayout(); await Task.Delay(20);
                var item = (ListBoxItem)_list.ItemContainerGenerator.ContainerFromItem(point)!; var location = item.PointToScreen(new Point(item.ActualWidth / 2, item.ActualHeight / 2));
                MouseProbe.SetCursorPos((int)location.X, (int)location.Y);
                for (var wait = 0; wait < 30 && !FavoriteHoverCardController.Shared.IsOpen; wait++) await Task.Delay(25);
                maxOpen = Math.Max(maxOpen, FavoriteHoverCardController.Shared.OpenPopupCount);
                enters &= FavoriteHoverCardController.Shared.IsOpen && FavoriteHoverCardController.Shared.Session.CurrentFavoriteId == point.Id;
                var away = _search.PointToScreen(new Point(5, 5)); MouseProbe.SetCursorPos((int)away.X, (int)away.Y); await Task.Delay(30);
                leaves &= !FavoriteHoverCardController.Shared.IsOpen;
            }
            result["MouseHoverTwentyCycles"] = enters && leaves && maxOpen <= 1; result["MaxOpenPopupCount"] = maxOpen; result["HoverLeaveCycles"] = 20;
            // 打开真实悬停后，分别验证搜索和列表滚动的关闭钩子。
            async Task Hover()
            {
                var point = targets[0]; _list.ScrollIntoView(point); _list.UpdateLayout(); var item = (ListBoxItem)_list.ItemContainerGenerator.ContainerFromItem(point)!;
                var position = item.PointToScreen(new Point(item.ActualWidth / 2, item.ActualHeight / 2)); MouseProbe.SetCursorPos((int)position.X, (int)position.Y); await Task.Delay(400);
            }
            await Hover(); _search.Text = "不存在的查询"; result["SearchClosesHover"] = !FavoriteHoverCardController.Shared.IsOpen; _search.Text = ""; await Task.Delay(250); await RefreshAsync();
            await Hover(); Descendants<ScrollViewer>(_list).First().ScrollToBottom(); _list.UpdateLayout(); await Task.Delay(50);
            result["ScrollClosesHover"] = !FavoriteHoverCardController.Shared.IsOpen;
            await Hover(); var another = new Window { Owner = owner, Width = 250, Height = 100, Title = "失焦验收" }; another.Show(); another.Activate(); await Task.Delay(50);
            result["DeactivatedClosesHover"] = !FavoriteHoverCardController.Shared.IsOpen; another.Close();
        }
        finally { CloseHover(); owner.Topmost = oldTopmost; MouseProbe.SetCursorPos(original.X, original.Y); if (oldForeground != IntPtr.Zero) MouseProbe.SetForegroundWindow(oldForeground); }
        return result;
    }
    internal static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    { if (root is T item) yield return item; for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child; }
    internal static class MouseProbe
    {
        [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
        [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    }
}
