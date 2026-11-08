using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using HikingGuide.Core.Favorites;

namespace HikingGuide.App;

/// <summary>完整名称按自然宽度换行，仅创建可见项与一行缓冲。统一间距8、内边距10/6。</summary>
public sealed class VirtualizingFavoriteWrapPanel : VirtualizingPanel, IScrollInfo
{
    private readonly Dictionary<string, Size> _nameSizes = new(StringComparer.Ordinal);
    private Rect[] _rects = [];
    private Size _extent, _viewport;
    private double _offset, _layoutWidth = -1;
    private bool _layoutDirty = true;
    protected override Size MeasureOverride(Size availableSize)
    {
        var owner = ItemsControl.GetItemsOwner(this); if (owner is null) return new();
        var width = Math.Max(40, double.IsFinite(availableSize.Width) ? availableSize.Width : ActualWidth);
        var height = Math.Max(0, double.IsFinite(availableSize.Height) ? availableSize.Height : ActualHeight);
        if (_layoutDirty || width != _layoutWidth || _rects.Length != owner.Items.Count) BuildLayout(owner, width);
        _viewport = new(width, height); _offset = Math.Clamp(_offset, 0, Math.Max(0, _extent.Height - height)); ScrollOwner?.InvalidateScrollInfo();
        var first = Array.FindIndex(_rects, r => r.Bottom >= Math.Max(0, _offset - 44)); if (first < 0) first = 0;
        var last = Array.FindLastIndex(_rects, r => r.Top <= _offset + height + 44);
        _ = InternalChildren.Count;
        var generator = (IItemContainerGenerator)owner.ItemContainerGenerator;
        var start = generator.GeneratorPositionFromIndex(first); var childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
        if (last >= first)
            using (generator.StartAt(start, GeneratorDirection.Forward, true))
                for (var index = first; index <= last; index++, childIndex++)
                {
                    var child = (UIElement)generator.GenerateNext(out var created);
                    if (created) { if (childIndex >= InternalChildren.Count) AddInternalChild(child); else InsertInternalChild(childIndex, child); generator.PrepareItemContainer(child); }
                    child.Measure(_rects[index].Size);
                }
        for (var i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0); var index = generator.IndexFromGeneratorPosition(position);
            if (index < first || index > last) { generator.Remove(position, 1); RemoveInternalChildRange(i, 1); }
        }
        return _viewport;
    }
    private void BuildLayout(ItemsControl owner, double width)
    {
        _layoutDirty = false; _layoutWidth = width; _rects = new Rect[owner.Items.Count];
        var measure = new TextBlock { FontFamily = owner.FontFamily, FontSize = owner.FontSize, TextWrapping = TextWrapping.Wrap };
        double x = 0, y = 0, rowHeight = 0;
        for (var i = 0; i < owner.Items.Count; i++)
        {
            var name = (owner.Items[i] as FavoritePoint)?.Name ?? "";
            if (!_nameSizes.TryGetValue(name, out var natural)) { measure.Text = name; measure.Measure(new(double.PositiveInfinity, double.PositiveInfinity)); natural = measure.DesiredSize; _nameSizes[name] = natural; }
            var itemWidth = Math.Min(width, Math.Max(40, natural.Width + 32)); var textHeight = natural.Height;
            if (natural.Width > itemWidth - 30) { measure.Text = name; measure.Measure(new(Math.Max(1, itemWidth - 30), double.PositiveInfinity)); textHeight = measure.DesiredSize.Height; }
            var itemHeight = Math.Max(32, textHeight + 14) + 8;
            if (x > 0 && x + itemWidth > width) { y += rowHeight; x = 0; rowHeight = 0; }
            _rects[i] = new(x, y, itemWidth, itemHeight); x += itemWidth; rowHeight = Math.Max(rowHeight, itemHeight);
        }
        _extent = new(width, y + rowHeight);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        var owner = ItemsControl.GetItemsOwner(this); if (owner is null) return finalSize;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var index = ((IItemContainerGenerator)owner.ItemContainerGenerator).IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index < 0 || index >= _rects.Length) continue;
            var rect = _rects[index]; rect.Offset(0, -_offset); InternalChildren[i].Arrange(rect);
        }
        return finalSize;
    }
    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    { base.OnItemsChanged(sender, args); if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) { RemoveInternalChildRange(0, InternalChildren.Count); _offset = 0; } _layoutDirty = true; InvalidateMeasure(); }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; } = true;
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _offset;
    public ScrollViewer? ScrollOwner { get; set; }
    public void SetHorizontalOffset(double offset) { }
    public void SetVerticalOffset(double offset) { _offset = Math.Clamp(offset, 0, Math.Max(0, ExtentHeight - ViewportHeight)); InvalidateMeasure(); ScrollOwner?.InvalidateScrollInfo(); }
    public void LineUp() => SetVerticalOffset(_offset - 40);
    public void LineDown() => SetVerticalOffset(_offset + 40);
    public void MouseWheelUp() => SetVerticalOffset(_offset - 120);
    public void MouseWheelDown() => SetVerticalOffset(_offset + 120);
    public void PageUp() => SetVerticalOffset(_offset - ViewportHeight);
    public void PageDown() => SetVerticalOffset(_offset + ViewportHeight);
    public void LineLeft() { } public void LineRight() { } public void MouseWheelLeft() { } public void MouseWheelRight() { } public void PageLeft() { } public void PageRight() { }
    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (visual is FrameworkElement element)
        {
            var owner = ItemsControl.GetItemsOwner(this); var container = ItemsControl.ContainerFromElement(owner, element);
            if (container is not null) { var index = owner.ItemContainerGenerator.IndexFromContainer(container); if (index >= 0 && index < _rects.Length) { var rect = _rects[index]; if (rect.Top < _offset) SetVerticalOffset(rect.Top); else if (rect.Bottom > _offset + ViewportHeight) SetVerticalOffset(rect.Bottom - ViewportHeight); } }
        }
        return rectangle;
    }
    protected override void BringIndexIntoView(int index)
    { if (index >= 0 && index < _rects.Length) SetVerticalOffset(_rects[index].Top); }
}
