using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace MusicPlayer.Controls;

/// <summary>A vertically scrolling gallery of uniform-width cards, realized only around the viewport.</summary>
public sealed class VirtualizingTilePanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(VirtualizingTilePanel),
        new FrameworkPropertyMetadata(184d, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => value is double width && double.IsFinite(width) && width > 0);

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    private double _rowHeight = 280;
    private int _columns = 1;
    private Size _extent;
    private Size _viewport;
    private double _offset;

    public VirtualizingTilePanel() => ClipToBounds = true;

    protected override Size MeasureOverride(Size availableSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner is null) return default;
        var count = owner.Items.Count;
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : ItemWidth;
        var height = double.IsFinite(availableSize.Height) ? availableSize.Height : _rowHeight;
        _columns = Math.Max(1, (int)(width / ItemWidth));

        if (count > 0)
        {
            // Cards in each gallery have the same layout. Measure one to retain its natural
            // height (including font/DPI changes), without measuring the entire collection.
            var sampleIndex = Math.Min(count - 1, (int)(_offset / _rowHeight) * _columns);
            var sample = Realize(sampleIndex);
            sample.Measure(new Size(ItemWidth, double.PositiveInfinity));
            _rowHeight = Math.Max(1, sample.DesiredSize.Height);
        }

        _viewport = new Size(width, height);
        _extent = new Size(width, Math.Ceiling(count / (double)_columns) * _rowHeight);
        _offset = Math.Clamp(_offset, 0, Math.Max(0, ExtentHeight - ViewportHeight));
        ScrollOwner?.InvalidateScrollInfo();

        // Keep one extra row on either side for smooth wheel scrolling.
        var first = Math.Max(0, (int)(_offset / _rowHeight) - 1) * _columns;
        var last = Math.Min(count - 1, ((int)Math.Ceiling((_offset + height) / _rowHeight) + 1) * _columns - 1);
        for (var index = first; index <= last; index++)
            Realize(index).Measure(new Size(ItemWidth, _rowHeight));

        for (var childIndex = InternalChildren.Count - 1; childIndex >= 0; childIndex--)
        {
            var position = new GeneratorPosition(childIndex, 0);
            var itemIndex = ItemContainerGenerator.IndexFromGeneratorPosition(position);
            if (itemIndex >= first && itemIndex <= last) continue;
            ItemContainerGenerator.Remove(position, 1);
            RemoveInternalChildRange(childIndex, 1);
        }
        return _viewport;
    }

    private UIElement Realize(int index)
    {
        var generator = ItemContainerGenerator;
        var position = generator.GeneratorPositionFromIndex(index);
        var childIndex = position.Offset == 0 ? position.Index : position.Index + 1;
        using (generator.StartAt(position, GeneratorDirection.Forward, true))
        {
            var child = (UIElement)generator.GenerateNext(out var isNew);
            if (isNew)
            {
                InsertInternalChild(childIndex, child);
                generator.PrepareItemContainer(child);
            }
            return child;
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (var childIndex = 0; childIndex < InternalChildren.Count; childIndex++)
        {
            var index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
            InternalChildren[childIndex].Arrange(new Rect(index % _columns * ItemWidth,
                index / _columns * _rowHeight - _offset, ItemWidth, _rowHeight));
        }
        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        // The generator has already updated its mapping. Discard the old visuals as a unit
        // so resets, removals and moves cannot leave stale cards or invalid positions.
        ItemContainerGenerator.RemoveAll();
        RemoveInternalChildRange(0, InternalChildren.Count);
        if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) _offset = 0;
        InvalidateMeasure();
    }

    protected override void BringIndexIntoView(int index)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner is null || index < 0 || index >= owner.Items.Count) return;
        var top = index / _columns * _rowHeight;
        if (top < _offset) SetVerticalOffset(top);
        else if (top + _rowHeight > _offset + ViewportHeight)
            SetVerticalOffset(top + _rowHeight - ViewportHeight);
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (visual == this || !IsAncestorOf(visual)) return Rect.Empty;
        var bounds = visual.TransformToAncestor(this).TransformBounds(rectangle);
        var top = bounds.Top + _offset;
        if (bounds.Top < 0) SetVerticalOffset(top);
        else if (bounds.Bottom > ViewportHeight) SetVerticalOffset(top + bounds.Height - ViewportHeight);
        bounds.Y = top - _offset;
        bounds.Intersect(new Rect(_viewport));
        return bounds;
    }

    public void SetVerticalOffset(double offset)
    {
        if (double.IsNaN(offset)) return;
        offset = Math.Clamp(offset, 0, Math.Max(0, ExtentHeight - ViewportHeight));
        if (_offset == offset) return;
        _offset = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public bool CanVerticallyScroll { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _offset;
    public ScrollViewer? ScrollOwner { get; set; }
    public void LineUp() => SetVerticalOffset(_offset - 24);
    public void LineDown() => SetVerticalOffset(_offset + 24);
    public void MouseWheelUp() => SetVerticalOffset(_offset - WheelDistance);
    public void MouseWheelDown() => SetVerticalOffset(_offset + WheelDistance);
    private double WheelDistance => SystemParameters.WheelScrollLines < 0
        ? ViewportHeight : SystemParameters.WheelScrollLines * 24;
    public void PageUp() => SetVerticalOffset(_offset - ViewportHeight);
    public void PageDown() => SetVerticalOffset(_offset + ViewportHeight);
    public void SetHorizontalOffset(double offset) { }
    public void LineLeft() { }
    public void LineRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void PageLeft() { }
    public void PageRight() { }
}
