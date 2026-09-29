using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Sink.Services;

namespace Sink.Controls;

public enum SuggestionField { None, Artist, Album, Genre, Title, Any }

/// <summary>
/// A dark-styled text box that drops down matching suggestions as you type and
/// completes on Tab / Enter. Pulls its list from <see cref="MetadataIndex"/>
/// (set <see cref="SuggestionField"/>) or from an explicit <see cref="Suggestions"/>.
/// </summary>
public class AutoCompleteTextBox : TextBox
{
    private static readonly BrushConverter Brushes = new();
    private static Brush Hex(string h) => (Brush)Brushes.ConvertFromString(h)!;

    private readonly Popup _popup;
    private readonly ListBox _list;
    private bool _suppress;

    public static readonly DependencyProperty SuggestionFieldProperty = DependencyProperty.Register(
        nameof(SuggestionField), typeof(SuggestionField), typeof(AutoCompleteTextBox),
        new PropertyMetadata(SuggestionField.None));

    public SuggestionField SuggestionField
    {
        get => (SuggestionField)GetValue(SuggestionFieldProperty);
        set => SetValue(SuggestionFieldProperty, value);
    }

    /// <summary>Explicit suggestion list; overrides <see cref="SuggestionField"/> when set.</summary>
    public IEnumerable<string>? Suggestions { get; set; }

    /// <summary>Suggestions read fresh on every keystroke (for lists that change while the box is in use); overrides everything else.</summary>
    public Func<IEnumerable<string>>? SuggestionProvider { get; set; }

    /// <summary>Most suggestions shown at once; the list scrolls past what fits.</summary>
    public int MaxSuggestions { get; set; } = 12;

    public AutoCompleteTextBox()
    {
        _list = new ListBox
        {
            MaxHeight = 220,
            Background = Hex("#1B1F28"),
            Foreground = Hex("#E4E7EC"),
            BorderThickness = new Thickness(0),
            FontSize = 12,
        };
        _list.PreviewMouseLeftButtonUp += (_, _) => AcceptSelected();

        _popup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = Hex("#161A22"),
                BorderBrush = Hex("#3A4250"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(2),
                Child = _list,
            },
        };

        TextChanged += OnTextChanged;
        LostFocus += (_, _) => Close();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private IReadOnlyList<string> Source() =>
        SuggestionProvider?.Invoke().ToList() ?? Suggestions?.ToList() ?? SuggestionField switch
        {
            SuggestionField.Artist => MetadataIndex.ArtistSuggestions(),
            SuggestionField.Album => MetadataIndex.AlbumSuggestions(),
            SuggestionField.Genre => MetadataIndex.GenreSuggestions(),
            SuggestionField.Title => MetadataIndex.TitleSuggestions(),
            SuggestionField.Any => MetadataIndex.ArtistSuggestions().Concat(MetadataIndex.AlbumSuggestions())
                .Concat(MetadataIndex.TitleSuggestions()).Concat(MetadataIndex.GenreSuggestions()).ToList(),
            _ => [],
        };

    private void OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppress || !IsKeyboardFocusWithin) return;
        var q = Text?.Trim() ?? "";
        if (q.Length == 0) { Close(); return; }

        // Narrows by prefix on every keystroke: "M" offers every value
        // starting with M, "My" drops "Modest Mouse", "My C" leaves only "My
        // Chemical Romance". Values where a later word starts with what's
        // typed ("Mouse" for "Mo") follow the whole-value matches, so "The
        // Beatles" is still found by "Bea". Mid-word hits are never offered.
        var all = Source();
        var starts = all.Where(x => x.StartsWith(q, StringComparison.OrdinalIgnoreCase));
        var wordStarts = all.Where(x => !x.StartsWith(q, StringComparison.OrdinalIgnoreCase) && HasWordStartingWith(x, q));
        var matches = starts.Concat(wordStarts)
            .Where(x => !x.Equals(q, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxSuggestions)
            .ToList();
        if (matches.Count == 0) { Close(); return; }

        _list.ItemsSource = matches;
        _list.SelectedIndex = 0;
        _popup.Width = ActualWidth;
        _popup.IsOpen = true;
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_popup.IsOpen) return;
        switch (e.Key)
        {
            case Key.Down:
                _list.SelectedIndex = Math.Min(_list.Items.Count - 1, _list.SelectedIndex + 1);
                _list.ScrollIntoView(_list.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up:
                _list.SelectedIndex = Math.Max(0, _list.SelectedIndex - 1);
                _list.ScrollIntoView(_list.SelectedItem);
                e.Handled = true;
                break;
            case Key.Tab:
            case Key.Enter:
                if (AcceptSelected()) e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    private bool AcceptSelected()
    {
        if (_list.SelectedItem is not string pick) return false;
        _suppress = true;
        Text = pick;
        CaretIndex = pick.Length;
        _suppress = false;
        Close();
        GetBindingExpression(TextProperty)?.UpdateSource();
        return true;
    }

    private static bool HasWordStartingWith(string value, string query)
    {
        for (var i = value.IndexOf(query, 1, StringComparison.OrdinalIgnoreCase); i > 0;
             i = i + 1 < value.Length ? value.IndexOf(query, i + 1, StringComparison.OrdinalIgnoreCase) : -1)
            if (!char.IsLetterOrDigit(value[i - 1])) return true;
        return false;
    }

    private void Close() => _popup.IsOpen = false;
}
