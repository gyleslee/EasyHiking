using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace HikingGuide.App;

public sealed partial class GuideWorkbenchControl
{
    private readonly Dictionary<DataGridColumn, string> _columnWidthKeys = [];
    private readonly DispatcherTimer _columnWidthSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private static readonly DependencyPropertyDescriptor ColumnWidthDescriptor =
        DependencyPropertyDescriptor.FromProperty(DataGridColumn.WidthProperty, typeof(DataGridColumn));
    private bool _restoringColumnWidths, _columnWidthsDirty;
    private Task _columnWidthSaveTask = Task.CompletedTask;
    private readonly HashSet<DataGridColumn> _autoFitPending = [];
    private bool _autoFitQueued;

    private void ObserveAutoFit(DataGrid grid)
    {
        grid.AddHandler(Control.MouseDoubleClickEvent, new MouseButtonEventHandler((_, e) =>
        {
            if (e.OriginalSource is not System.Windows.DependencyObject source || FindAncestor<Thumb>(source) is not { } thumb ||
                !thumb.Name.Contains("HeaderGripper", StringComparison.Ordinal)) return;
            if (FindAncestor<DataGridColumnHeader>(thumb)?.Column is { } column) QueueAutoFit(column);
        }), true);
    }

    private void QueueAutoFit(DataGridColumn column)
    {
        _autoFitPending.Add(column);
        if (_autoFitQueued) return; _autoFitQueued = true;
        Dispatcher.BeginInvoke(() => { _autoFitQueued = false; CaptureAutoFitWidths(); }, DispatcherPriority.ContextIdle);
    }

    private void CaptureAutoFitWidths()
    {
        _table.UpdateLayout(); _days.UpdateLayout();
        foreach (var column in _autoFitPending.ToArray())
        {
            _autoFitPending.Remove(column);
            if (!_columnWidthKeys.ContainsKey(column) || !double.IsFinite(column.ActualWidth) || column.ActualWidth <= 0) continue;
            // 双击自动适配的结果按实际像素宽度固定及保存，避免虚拟化内容再改变用户选择。
            column.Width = Math.Clamp(column.ActualWidth, column.MinWidth, column.MaxWidth);
            OnColumnWidthChanged(column, EventArgs.Empty);
        }
    }

    private void RegisterColumnWidth(DataGridColumn column, string key)
    {
        column.MaxWidth = 4000;
        if (Host.GlobalSettingsService.Current.GuideTableColumnWidths.TryGetValue(key, out var width))
            column.Width = Math.Max(column.MinWidth, width);
        _columnWidthKeys[column] = key;
        ColumnWidthDescriptor.AddValueChanged(column, OnColumnWidthChanged);
    }

    private void UnregisterColumnWidth(DataGridColumn column)
    {
        ColumnWidthDescriptor.RemoveValueChanged(column, OnColumnWidthChanged);
        _columnWidthKeys.Remove(column);
    }

    private void RestoreRegisteredColumnWidths()
    {
        _restoringColumnWidths = true;
        try
        {
            foreach (var (column, key) in _columnWidthKeys)
                if (Host.GlobalSettingsService.Current.GuideTableColumnWidths.TryGetValue(key, out var width))
                    column.Width = Math.Max(column.MinWidth, width);
        }
        finally { _restoringColumnWidths = false; }
    }

    private void OnColumnWidthChanged(object? sender, EventArgs e)
    {
        if (_restoringColumnWidths || _loading || sender is not DataGridColumn column || !_columnWidthKeys.TryGetValue(column, out var key)) return;
        if (column.Width.IsAuto || column.Width.IsSizeToCells || column.Width.IsSizeToHeader) { QueueAutoFit(column); return; }
        if (!column.Width.IsAbsolute) return;
        var width = column.Width.Value;
        if (!double.IsFinite(width) || width < 20 || width > 4000) return;
        var settings = Host.GlobalSettingsService.Current;
        if (settings.GuideTableColumnWidths.TryGetValue(key, out var old) && Math.Abs(old - width) < .1) return;
        // 替换字典而不是原地修改，避免后台设置序列化枚举时发生竞争。
        settings.GuideTableColumnWidths = new(settings.GuideTableColumnWidths, StringComparer.Ordinal) { [key] = width };
        _columnWidthsDirty = true;
        _columnWidthSaveTimer.Stop(); _columnWidthSaveTimer.Start();
    }

    public async Task FlushColumnWidthsAsync()
    {
        _columnWidthSaveTimer.Stop();
        if (_autoFitPending.Count > 0) CaptureAutoFitWidths();
        await _columnWidthSaveTask;
        if (!_columnWidthsDirty) return;
        _columnWidthsDirty = false;
        _columnWidthSaveTask = SaveColumnWidthsAsync();
        await _columnWidthSaveTask;
    }

    private async Task SaveColumnWidthsAsync()
    {
        try { await Host.GlobalSettingsService.SaveAsync(); }
        catch (Exception error)
        { _columnWidthsDirty = true; _status.Text = "表格列宽未能保存，下次启动可能恢复原宽度：" + error.Message; }
    }
}
