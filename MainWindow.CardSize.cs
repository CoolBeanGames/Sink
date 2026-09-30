using System.Windows;
using System.Windows.Media;
using Sink.Controls;
using Sink.Services;

namespace Sink;

/// <summary>
/// The artwork-size slider for the album / artist / genre grid: slide right
/// for bigger cards (fewer per row), left for smaller ones (more per row), and
/// all the way left for a compact list — one row per album/artist/genre with
/// a small thumbnail — until it's slid back up. The card template scales with
/// <see cref="CardScale"/> and the virtualizing grid's cell size follows it.
/// Remembered in settings.
/// </summary>
public partial class MainWindow
{
    public static readonly DependencyProperty CardScaleProperty = DependencyProperty.Register(
        nameof(CardScale), typeof(double), typeof(MainWindow), new PropertyMetadata(1.0));

    /// <summary>How much the album/artist/genre cards are scaled (bound by the card template).</summary>
    public double CardScale
    {
        get => (double)GetValue(CardScaleProperty);
        set => SetValue(CardScaleProperty, value);
    }

    /// <summary>Below this slider value the grid becomes a list.</summary>
    private const double CardListThreshold = 5;
    private const double CardBaseWidth = 184, CardBaseHeight = 192, CardCellPadding = 20, ListRowHeight = 70; // 46 px row + 12 margin + 8 padding + 4 selection border

    private DataTemplate? _cardTemplate;
    private bool _cardSizeReady;

    /// <summary>Called once the window's controls exist: restores the saved size.</summary>
    private void InitCardSize()
    {
        _cardTemplate = GroupsView.ItemTemplate;
        _cardSizeReady = true;
        CardSizeSlider.Value = Math.Clamp(AppSettings.Current.CardSize, 0, 100);
        GroupsScroller.SizeChanged += (_, _) => { if (IsCardListMode) ApplyCardSize(); };
        ApplyCardSize();
    }

    private bool IsCardListMode => CardSizeSlider.Value < CardListThreshold;

    private void CardSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_cardSizeReady) return;
        AppSettings.Current.CardSize = CardSizeSlider.Value; // saved with the other settings on close
        ApplyCardSize();
    }

    private void ApplyCardSize()
    {
        if (_cardTemplate is null) return;
        var panel = FindVisualChild<VirtualizingWrapPanel>(GroupsView);
        if (IsCardListMode)
        {
            if (!ReferenceEquals(GroupsView.ItemTemplate, FindResource("GroupRowTemplate")))
                GroupsView.ItemTemplate = (DataTemplate)FindResource("GroupRowTemplate");
            if (panel is not null)
            {
                panel.ItemWidth = Math.Max(240, GroupsScroller.ActualWidth - 24);
                panel.ItemHeight = ListRowHeight;
            }
        }
        else
        {
            if (!ReferenceEquals(GroupsView.ItemTemplate, _cardTemplate)) GroupsView.ItemTemplate = _cardTemplate;
            // 5 … 100 on the slider → 0.55× … 1.65× the default card (43 ≈ 1×).
            var scale = 0.55 + (CardSizeSlider.Value - CardListThreshold) / (100 - CardListThreshold) * 1.1;
            CardScale = scale;
            if (panel is not null)
            {
                panel.ItemWidth = CardBaseWidth * scale + CardCellPadding;
                panel.ItemHeight = CardBaseHeight * scale + CardCellPadding;
            }
        }
        // The grid's panel only exists once the list has been shown; try again then.
        if (panel is null) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            if (FindVisualChild<VirtualizingWrapPanel>(GroupsView) is not null) ApplyCardSize();
        });
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } found) return found;
        }
        return null;
    }
}
