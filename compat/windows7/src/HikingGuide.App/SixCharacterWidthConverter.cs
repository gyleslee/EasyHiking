using System.Globalization;
using System.Windows.Data;

namespace HikingGuide.App;

/// <summary>以当前字体六个中文全角字符为属性名称列的内容宽度，随DPI缩放。</summary>
public sealed class SixCharacterWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is double size ? size * 6 : 72d;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
