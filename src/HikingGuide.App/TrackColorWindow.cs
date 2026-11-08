using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using HikingGuide.Application.Maps;

namespace HikingGuide.App;

public sealed class TrackColorWindow : Window
{
    private const double PlaneWidth = 355;
    private const double PlaneHeight = 245;
    private readonly Canvas _plane;
    private readonly Canvas _hueBar;
    private readonly Ellipse _planeOuter;
    private readonly Ellipse _planeInner;
    private readonly Rectangle _hueIndicator;
    private readonly Border _preview;
    private readonly TextBox _input;
    private readonly TextBlock _rgb;
    private readonly TextBlock _hint;
    private double _hue;
    private double _saturation;
    private double _value;
    private bool _updatingInput;

    public TrackColorWindow(string initialColor)
    {
        Title = "设置轨迹颜色 - 颜色选择器";
        Width = 575;
        Height = 435;
        MinWidth = Width;
        MinHeight = Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new Thickness(17) };
        panel.Children.Add(new TextBlock
        {
            Text = "在左侧选择明暗与鲜艳度，在彩虹色条选择色相。也可输入颜色代码。",
            Margin = new Thickness(0, 0, 0, 9)
        });
        var pickerRow = new StackPanel { Orientation = Orientation.Horizontal };
        _plane = new Canvas { Width = PlaneWidth, Height = PlaneHeight, Cursor = Cursors.Cross };
        var white = new Rectangle { Width = PlaneWidth, Height = PlaneHeight, Fill = Gradient(
            (Colors.White, 0), (Color.FromArgb(0, 255, 255, 255), 1), false) };
        var black = new Rectangle { Width = PlaneWidth, Height = PlaneHeight, Fill = Gradient(
            (Color.FromArgb(0, 0, 0, 0), 0), (Colors.Black, 1), true) };
        _plane.Children.Add(white);
        _plane.Children.Add(black);
        _planeOuter = new Ellipse { Width = 16, Height = 16, Stroke = Brushes.Black,
            StrokeThickness = 2, Fill = Brushes.Transparent, IsHitTestVisible = false };
        _planeInner = new Ellipse { Width = 12, Height = 12, Stroke = Brushes.White,
            StrokeThickness = 2, Fill = Brushes.Transparent, IsHitTestVisible = false };
        _plane.Children.Add(_planeOuter);
        _plane.Children.Add(_planeInner);
        WireDrag(_plane, position =>
        {
            _saturation = Math.Clamp(position.X / PlaneWidth, 0, 1);
            _value = 1 - Math.Clamp(position.Y / PlaneHeight, 0, 1);
            RenderColor();
        });
        pickerRow.Children.Add(_plane);

        _hueBar = new Canvas { Width = 28, Height = PlaneHeight, Margin = new Thickness(12, 0, 0, 0), Cursor = Cursors.Hand,
            Background = HueGradient() };
        _hueIndicator = new Rectangle { Width = 32, Height = 8, Fill = Brushes.Transparent,
            Stroke = Brushes.Black, StrokeThickness = 2, IsHitTestVisible = false };
        _hueBar.Children.Add(_hueIndicator);
        WireDrag(_hueBar, position =>
        {
            _hue = Math.Clamp(position.Y / PlaneHeight, 0, 1) * 360;
            RenderColor();
        });
        pickerRow.Children.Add(_hueBar);

        var details = new StackPanel { Width = 105, Margin = new Thickness(13, 0, 0, 0) };
        details.Children.Add(new TextBlock { Text = "当前颜色", Margin = new Thickness(0, 0, 0, 5) });
        _preview = new Border { Width = 100, Height = 72, BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left };
        details.Children.Add(_preview);
        _rgb = new TextBlock { Margin = new Thickness(0, 12, 0, 0), LineHeight = 21 };
        details.Children.Add(_rgb);
        pickerRow.Children.Add(details);
        panel.Children.Add(pickerRow);

        var inputRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        inputRow.Children.Add(new TextBlock { Text = "颜色代码：", VerticalAlignment = VerticalAlignment.Center });
        _input = new TextBox { Width = 120, Height = 27, VerticalContentAlignment = VerticalAlignment.Center };
        _input.TextChanged += (_, _) => OnInputChanged();
        inputRow.Children.Add(_input);
        _hint = new TextBlock { Foreground = Brushes.DarkRed, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0) };
        inputRow.Children.Add(_hint);
        panel.Children.Add(inputRow);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 11, 0, 0) };
        buttons.Children.Add(new Button { Content = "取消", Width = 75, IsCancel = true });
        var accept = new Button { Content = "确定", Width = 75, Margin = new Thickness(8, 0, 0, 0), IsDefault = true };
        accept.Click += (_, _) =>
        {
            if (!TrackDisplayColor.IsValid(_input.Text))
            {
                _hint.Text = "请输入有效的 #RRGGBB 颜色。";
                return;
            }
            SelectedColor = TrackDisplayColor.Normalize(_input.Text);
            DialogResult = true;
        };
        buttons.Children.Add(accept);
        panel.Children.Add(buttons);
        Content = panel;

        var initial = TrackDisplayColor.IsValid(initialColor) ? initialColor : "#2877C7";
        FromRgb((Color)ColorConverter.ConvertFromString(initial)!);
        RenderColor();
    }

    public string SelectedColor { get; private set; } = "#2877C7";

    private void OnInputChanged()
    {
        if (_updatingInput) return;
        if (!TrackDisplayColor.IsValid(_input.Text))
        {
            _hint.Text = "颜色格式：#RRGGBB";
            return;
        }
        _hint.Text = "";
        FromRgb((Color)ColorConverter.ConvertFromString(_input.Text)!);
        RenderColor(updateInput: false);
    }

    private void RenderColor(bool updateInput = true)
    {
        var hueColor = ToRgb(_hue, 1, 1);
        var color = ToRgb(_hue, _saturation, _value);
        _plane.Background = new SolidColorBrush(hueColor);
        _preview.Background = new SolidColorBrush(color);
        _rgb.Text = $"红：{color.R}\n绿：{color.G}\n蓝：{color.B}";
        Canvas.SetLeft(_planeOuter, _saturation * PlaneWidth - _planeOuter.Width / 2);
        Canvas.SetTop(_planeOuter, (1 - _value) * PlaneHeight - _planeOuter.Height / 2);
        Canvas.SetLeft(_planeInner, _saturation * PlaneWidth - _planeInner.Width / 2);
        Canvas.SetTop(_planeInner, (1 - _value) * PlaneHeight - _planeInner.Height / 2);
        Canvas.SetLeft(_hueIndicator, -2);
        Canvas.SetTop(_hueIndicator, _hue / 360 * PlaneHeight - _hueIndicator.Height / 2);
        if (!updateInput) return;
        _updatingInput = true;
        _input.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        _updatingInput = false;
        _hint.Text = "";
    }

    private void FromRgb(Color color)
    {
        var r = color.R / 255d; var g = color.G / 255d; var b = color.B / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        _value = max;
        _saturation = max == 0 ? 0 : delta / max;
        _hue = delta == 0 ? 0 : max == r ? 60 * (((g - b) / delta) % 6) :
            max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        if (_hue < 0) _hue += 360;
    }

    private static Color ToRgb(double hue, double saturation, double value)
    {
        var chroma = value * saturation;
        var x = chroma * (1 - Math.Abs((hue / 60) % 2 - 1));
        var m = value - chroma;
        var (r, g, b) = hue switch
        {
            < 60 => (chroma, x, 0d),
            < 120 => (x, chroma, 0d),
            < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma),
            < 300 => (x, 0d, chroma),
            _ => (chroma, 0d, x)
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    private static LinearGradientBrush Gradient((Color Color, double Offset) start,
        (Color Color, double Offset) end, bool vertical)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0),
            EndPoint = vertical ? new Point(0, 1) : new Point(1, 0) };
        brush.GradientStops.Add(new GradientStop(start.Color, start.Offset));
        brush.GradientStops.Add(new GradientStop(end.Color, end.Offset));
        return brush;
    }

    private static LinearGradientBrush HueGradient()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        var colors = new[] { Colors.Red, Colors.Yellow, Colors.Lime, Colors.Cyan, Colors.Blue, Colors.Magenta, Colors.Red };
        for (var index = 0; index < colors.Length; index++)
            brush.GradientStops.Add(new GradientStop(colors[index], index / 6d));
        return brush;
    }

    private static void WireDrag(Canvas canvas, Action<Point> update)
    {
        canvas.MouseLeftButtonDown += (_, e) => { canvas.CaptureMouse(); update(e.GetPosition(canvas)); e.Handled = true; };
        canvas.MouseMove += (_, e) => { if (canvas.IsMouseCaptured) update(e.GetPosition(canvas)); };
        canvas.MouseLeftButtonUp += (_, e) =>
        {
            if (!canvas.IsMouseCaptured) return;
            update(e.GetPosition(canvas));
            canvas.ReleaseMouseCapture();
            e.Handled = true;
        };
    }
}
