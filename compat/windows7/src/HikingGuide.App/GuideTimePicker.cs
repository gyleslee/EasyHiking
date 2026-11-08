using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace HikingGuide.App;

/// <summary>滚动式小时/分钟选择器。取消不提交，确定后双向绑定立即通知行程模型。</summary>
public sealed class GuideTimePicker : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(string), typeof(GuideTimePicker),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((GuideTimePicker)d).RefreshText()));
    public string Value { get => (string)GetValue(ValueProperty); set => SetCurrentValue(ValueProperty, value); }
    private readonly Button _input = new() { MinHeight = 22, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new(3, 0, 3, 0), Foreground = Brushes.Black };
    private readonly Popup _popup = new() { Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
    internal ListBox Hours { get; } = new() { Width = 86, Height = 170, ItemsSource = Enumerable.Range(0, 24).Select(i => i.ToString("00")).ToArray() };
    internal ListBox Minutes { get; } = new() { Width = 86, Height = 170, ItemsSource = Enumerable.Range(0, 60).Select(i => i.ToString("00")).ToArray() };
    internal Button ConfirmButton { get; } = new() { Content = "确定", MinWidth = 55, Padding = new(8, 4, 8, 4), IsDefault = true };
    internal Button CancelButton { get; } = new() { Content = "取消", MinWidth = 55, Padding = new(8, 4, 8, 4) };
    internal Button ClearButton { get; } = new() { Content = "清空", MinWidth = 55, Padding = new(8, 4, 8, 4) };
    internal bool IsOpen => _popup.IsOpen;
    internal FrameworkElement PickerPanel => (FrameworkElement)_popup.Child;
    public GuideTimePicker()
    {
        Content = _input; _popup.PlacementTarget = _input;
        var panel = new DockPanel { Background = Brushes.White, Margin = new(8) };
        var wheels = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(wheels, Dock.Top); panel.Children.Add(wheels);
        foreach (var (title, list) in new[] { ("小时", Hours), ("分钟", Minutes) })
        {
            var group = new StackPanel(); group.Children.Add(new TextBlock { Text = title, TextAlignment = TextAlignment.Center, Margin = new(0, 0, 0, 5) });
            var style = new Style(typeof(ListBoxItem)); style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center)); style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 34d));
            var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true }; selected.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Bold)); style.Triggers.Add(selected);
            list.ItemContainerStyle = style; list.BorderThickness = new(0); list.FontSize = 16;
            var itemsPanel = new FrameworkElementFactory(typeof(StackPanel)); itemsPanel.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 68, 0, 68));
            list.ItemsPanel = new ItemsPanelTemplate(itemsPanel); ScrollViewer.SetCanContentScroll(list, false);
            list.SelectionChanged += (_, _) => list.Dispatcher.BeginInvoke(() => CenterSelection(list), DispatcherPriority.Loaded);
            list.PreviewMouseWheel += (_, e) => { list.SelectedIndex = Math.Clamp(list.SelectedIndex + (e.Delta > 0 ? -1 : 1), 0, list.Items.Count - 1); list.ScrollIntoView(list.SelectedItem); e.Handled = true; };
            group.Children.Add(list); wheels.Children.Add(group);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 8, 0, 0) };
        foreach (var button in new[] { ClearButton, CancelButton, ConfirmButton }) { button.Margin = new(3, 0, 0, 0); buttons.Children.Add(button); }
        panel.Children.Add(buttons);
        _popup.Child = new Border { Background = Brushes.White, BorderBrush = Brushes.Gray, BorderThickness = new(1), CornerRadius = new(4), Child = panel };
        _input.Click += (_, _) => Open();
        ConfirmButton.Click += (_, _) => { Value = Hours.SelectedItem + ":" + Minutes.SelectedItem; GetBindingExpression(ValueProperty)?.UpdateSource(); _popup.IsOpen = false; };
        CancelButton.Click += (_, _) => _popup.IsOpen = false;
        ClearButton.Click += (_, _) => { Value = ""; GetBindingExpression(ValueProperty)?.UpdateSource(); _popup.IsOpen = false; };
        _popup.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { _popup.IsOpen = false; e.Handled = true; }
            else if (e.Key == Key.Enter && e.OriginalSource is not Button) { ConfirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); e.Handled = true; }
        };
        Unloaded += (_, _) => _popup.IsOpen = false;
        RefreshText();
    }
    private void RefreshText() => _input.Content = string.IsNullOrEmpty(Value) ? "选择时间 ▾" : Value + " ▾";
    internal void Open()
    {
        var time = TimeOnly.TryParseExact(Value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var saved) ? saved : new TimeOnly(7, 0);
        Hours.SelectedIndex = time.Hour; Minutes.SelectedIndex = time.Minute; _popup.IsOpen = true;
        Hours.ScrollIntoView(Hours.SelectedItem); Minutes.ScrollIntoView(Minutes.SelectedItem);
        Dispatcher.BeginInvoke(() => { CenterSelection(Hours); CenterSelection(Minutes); }, DispatcherPriority.Loaded);
    }
    private static void CenterSelection(ListBox list)
    {
        list.UpdateLayout(); FavoritesPanel.Descendants<ScrollViewer>(list).FirstOrDefault()?.ScrollToVerticalOffset(Math.Max(0, list.SelectedIndex * 34));
    }
    public void FocusInput() => _input.Focus();
}
