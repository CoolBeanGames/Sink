using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Sink.Controls;

/// <summary>
/// A fixed-cell wrapping panel that realizes only the visible rows (plus one
/// row of cache on either side). WPF's stock WrapPanel measures every child,
/// so putting one inside a ScrollViewer creates every album/artist card even
/// when only a handful are on screen.
/// </summary>
public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(204d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(212d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    private Size _extent;
    private Size _viewport;
    private Point _offset;
    private int _itemsPerRow = 1;

    protected override Size MeasureOverride(Size availableSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        var itemCount = owner?.Items.Count ?? 0;
        var cellWidth = ValidCellSize(ItemWidth, 204);
        var cellHeight = ValidCellSize(ItemHeight, 212);
        var viewportWidth = ViewportDimension(availableSize.Width, ScrollOwner?.ViewportWidth, ActualWidth, cellWidth);
        var viewportHeight = ViewportDimension(availableSize.Height, ScrollOwner?.ViewportHeight, ActualHeight, cellHeight * 2);
        var itemsPerRow = Math.Max(1, (int)Math.Floor(viewportWidth / cellWidth));

        UpdateScrollData(itemCount, itemsPerRow, viewportWidth, viewportHeight, cellHeight);

        if (itemCount == 0)
        {
            CleanupItems(0, -1);
            return _viewport;
        }

        var firstVisibleRow = Math.Max(0, (int)Math.Floor(_offset.Y / cellHeight));
        var lastVisibleRow = Math.Max(firstVisibleRow,
            (int)Math.Ceiling((_offset.Y + _viewport.Height) / cellHeight) - 1);
        var firstRealizedRow = Math.Max(0, firstVisibleRow - 1);
        var lastRow = Math.Max(0, (int)Math.Ceiling((double)itemCount / _itemsPerRow) - 1);
        var lastRealizedRow = Math.Min(lastRow, lastVisibleRow + 1);
        var firstIndex = firstRealizedRow * _itemsPerRow;
        var lastIndex = Math.Min(itemCount - 1, ((lastRealizedRow + 1) * _itemsPerRow) - 1);

        RealizeItems(firstIndex, lastIndex, cellWidth, cellHeight);
        // Generate the new viewport before recycling the old one. Removing the
        // old generator positions first can make a far-away ScrollIntoView
        // report recycled containers without inserting them back into the
        // visual children collection.
        CleanupItems(firstIndex, lastIndex);
        return _viewport;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var cellWidth = ValidCellSize(ItemWidth, 204);
        var cellHeight = ValidCellSize(ItemHeight, 212);
        var generator = ItemContainerGenerator;

        for (var childIndex = 0; childIndex < InternalChildren.Count; childIndex++)
        {
            var itemIndex = generator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
            if (itemIndex < 0) continue;
            var row = itemIndex / _itemsPerRow;
            var column = itemIndex % _itemsPerRow;
            InternalChildren[childIndex].Arrange(new Rect(
                column * cellWidth,
                row * cellHeight - _offset.Y,
                cellWidth,
                cellHeight));
        }

        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        InvalidateMeasure();
    }

    protected override void BringIndexIntoView(int index)
    {
        if (index < 0) return;
        var row = index / Math.Max(1, _itemsPerRow);
        var top = row * ValidCellSize(ItemHeight, 212);
        var bottom = top + ValidCellSize(ItemHeight, 212);
        if (top < _offset.Y) SetVerticalOffset(top);
        else if (bottom > _offset.Y + _viewport.Height) SetVerticalOffset(bottom - _viewport.Height);
    }

    private void RealizeItems(int firstIndex, int lastIndex, double cellWidth, double cellHeight)
    {
        if (firstIndex > lastIndex) return;
        var generator = ItemContainerGenerator;
        if (generator is null)
        {
            Dispatcher.BeginInvoke(InvalidateMeasure);
            return;
        }
        var start = generator.GeneratorPositionFromIndex(firstIndex);
        var childIndex = start.Offset == 0 ? start.Index : start.Index + 1;

        using (generator.StartAt(start, GeneratorDirection.Forward, allowStartAtRealizedItem: true))
        {
            for (var itemIndex = firstIndex; itemIndex <= lastIndex; itemIndex++, childIndex++)
            {
                if (generator.GenerateNext(out var newlyRealized) is not UIElement child) continue;
                if (newlyRealized)
                {
                    if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                    else InsertInternalChild(childIndex, child);
                    generator.PrepareItemContainer(child);
                }
                child.Measure(new Size(cellWidth, cellHeight));
            }
        }
    }

    private void CleanupItems(int firstIndex, int lastIndex)
    {
        var generator = ItemContainerGenerator;
        if (generator is null) return;
        for (var childIndex = InternalChildren.Count - 1; childIndex >= 0; childIndex--)
        {
            var position = new GeneratorPosition(childIndex, 0);
            var itemIndex = generator.IndexFromGeneratorPosition(position);
            if (itemIndex >= firstIndex && itemIndex <= lastIndex) continue;

            if (generator is IRecyclingItemContainerGenerator recycling)
                recycling.Recycle(position, 1);
            else
                generator.Remove(position, 1);
            RemoveInternalChildRange(childIndex, 1);
        }
    }

    private void UpdateScrollData(int itemCount, int itemsPerRow, double viewportWidth, double viewportHeight, double cellHeight)
    {
        var firstVisibleIndex = (int)Math.Floor(_offset.Y / cellHeight) * Math.Max(1, _itemsPerRow);
        if (itemsPerRow != _itemsPerRow)
            _offset.Y = Math.Floor((double)firstVisibleIndex / itemsPerRow) * cellHeight;

        _itemsPerRow = itemsPerRow;
        var rows = itemCount == 0 ? 0 : (int)Math.Ceiling((double)itemCount / itemsPerRow);
        var extent = new Size(viewportWidth, rows * cellHeight);
        var viewport = new Size(viewportWidth, viewportHeight);
        var maxOffset = Math.Max(0, extent.Height - viewport.Height);
        _offset.Y = Math.Clamp(_offset.Y, 0, maxOffset);

        if (_extent != extent || _viewport != viewport)
        {
            _extent = extent;
            _viewport = viewport;
            ScrollOwner?.InvalidateScrollInfo();
        }
    }

    private static double ValidCellSize(double value, double fallback) =>
        double.IsFinite(value) && value > 0 ? value : fallback;

    private static double ViewportDimension(double available, double? scrollOwner, double actual, double fallback)
    {
        if (double.IsFinite(available) && available > 0) return available;
        if (scrollOwner is > 0 and < double.PositiveInfinity) return scrollOwner.Value;
        if (double.IsFinite(actual) && actual > 0) return actual;
        return fallback;
    }

    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; } = true;
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;
    public ScrollViewer? ScrollOwner { get; set; }

    public void LineUp() => SetVerticalOffset(_offset.Y - 48);
    public void LineDown() => SetVerticalOffset(_offset.Y + 48);
    public void LineLeft() { }
    public void LineRight() { }
    public void MouseWheelUp() => SetVerticalOffset(_offset.Y - 144);
    public void MouseWheelDown() => SetVerticalOffset(_offset.Y + 144);
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);
    public void PageLeft() { }
    public void PageRight() { }
    public void SetHorizontalOffset(double offset) { }

    public void SetVerticalOffset(double offset)
    {
        var value = Math.Clamp(offset, 0, Math.Max(0, _extent.Height - _viewport.Height));
        if (Math.Abs(value - _offset.Y) < 0.1) return;
        _offset.Y = value;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        DependencyObject? child = visual;
        while (child is not null && VisualTreeHelper.GetParent(child) != this)
            child = VisualTreeHelper.GetParent(child);
        if (child is not UIElement element) return rectangle;

        var childIndex = InternalChildren.IndexOf(element);
        if (childIndex < 0) return rectangle;
        var itemIndex = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
        BringIndexIntoView(itemIndex);
        return new Rect(0, itemIndex / Math.Max(1, _itemsPerRow) * ItemHeight, ItemWidth, ItemHeight);
    }
}
