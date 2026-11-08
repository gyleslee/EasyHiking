using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace HikingGuide.App;

public sealed partial class GuideWorkbenchControl
{
    internal string KeyboardNavigationTrace { get; private set; } = "未收到方向键";
    private static Style CenteredTextStyle()
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        style.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brushes.Black));
        return style;
    }

    private static Style CenteredEditorStyle(bool multiline)
    {
        var style = new Style(typeof(TextBox));
        style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
        style.Setters.Add(new Setter(TextBox.TextWrappingProperty, TextWrapping.Wrap));
        style.Setters.Add(new Setter(TextBox.AcceptsReturnProperty, multiline));
        // 默认MaxLength=0，不截断人工路况/备注。
        return style;
    }

    private sealed class BoundaryTooltipConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            new ToolTip { Content = new TextBlock { Text = value?.ToString() ?? "", TextWrapping = TextWrapping.Wrap, MaxWidth = 460 } };
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    private static DataGridColumn CreateDepartureColumn()
    {
        var factory = new FrameworkElementFactory(typeof(GuideTimePicker));
        factory.SetBinding(GuideTimePicker.ValueProperty, new Binding(nameof(DayRow.Departure))
        { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, ValidatesOnExceptions = true });
        return new DataGridTemplateColumn { CellTemplate = new DataTemplate { VisualTree = factory }, IsReadOnly = true };
    }

    private void EnableKeyboardEditing(DataGrid grid)
    {
        grid.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key is not (Key.Up or Key.Down or Key.Left or Key.Right) || Keyboard.Modifiers != ModifierKeys.None) return;
            KeyboardNavigationTrace = "收到:" + e.Key + ";焦点:" + Keyboard.FocusedElement?.GetType().Name;
            if (e.OriginalSource is DependencyObject source && FindAncestor<ComboBox>(source) is { IsDropDownOpen: true }) return;
            var row = grid.Items.IndexOf(grid.CurrentItem); var column = grid.CurrentColumn;
            if (row < 0 || column is null) return;
            var columns = grid.Columns.Where(c => c.Visibility == Visibility.Visible).OrderBy(c => c.DisplayIndex).ToArray();
            var index = Array.IndexOf(columns, column); if (index < 0) return;
            var nextRow = Math.Clamp(row + (e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0), 0, grid.Items.Count - 1);
            var nextCol = Math.Clamp(index + (e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0), 0, columns.Length - 1);
            e.Handled = true; if (!CommitEdits()) { KeyboardNavigationTrace += ";提交失败:" + _status.Text; return; }
            // 先执行上一格提交排队的刷新，再进入目标格；否则刷新会把新编辑器提交并关闭。
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            var item = grid.Items[nextRow]; var target = columns[nextCol];
            grid.SelectedItem = item; grid.CurrentCell = new(item, target); grid.ScrollIntoView(item, target);
            await Dispatcher.InvokeAsync(grid.UpdateLayout, DispatcherPriority.Loaded);
            var content = target.GetCellContent(item); var cell = content is null ? null : FindAncestor<DataGridCell>(content);
            cell?.Focus();
            if (!target.IsReadOnly && grid.BeginEdit())
            {
                await Dispatcher.InvokeAsync(grid.UpdateLayout, DispatcherPriority.Loaded);
                var editor = target.GetCellContent(item) as TextBox;
                if (editor is not null) { editor.Focus(); editor.SelectAll(); }
            }
            else if ((content as ComboBox ?? (content is null ? null : FavoritesPanel.Descendants<ComboBox>(content).FirstOrDefault())) is { } combo) combo.Focus();
            else if (content is not null && FavoritesPanel.Descendants<GuideTimePicker>(content).FirstOrDefault() is { } picker) picker.FocusInput();
            KeyboardNavigationTrace += ";目标行:" + nextRow + ";列:" + target.SortMemberPath + ";焦点:" + Keyboard.FocusedElement?.GetType().Name;
        };
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T result) return result;
            current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }
}
