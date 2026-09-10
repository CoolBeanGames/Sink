using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Sink.Services;

namespace Sink.Controls;

public enum SuggestionField { None, Artist, Album, Genre }

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
        Suggestions?.ToList() ?? SuggestionField switch
        {
            SuggestionField.Artist => MetadataIndex.Artists,
            SuggestionField.Album => MetadataIndex.Albums,
            SuggestionField.Genre => MetadataIndex.Genres,
            _ => [],
        };

    private void OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppress || !IsKeyboardFocusWithin) return;
        var q = Text?.Trim() ?? "";
        if (q.Length == 0) { Close(); return; }

        var all = Source();
        var starts = all.Where(x => x.StartsWith(q, StringComparison.OrdinalIgnoreCase)
                                    && !x.Equals(q, StringComparison.OrdinalIgnoreCase));
        var contains = all.Where(x => !x.StartsWith(q, StringComparison.OrdinalIgnoreCase)
                                      && x.Contains(q, StringComparison.OrdinalIgnoreCase));
        var matches = starts.Concat(contains).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
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

    private void Close() => _popup.IsOpen = false;
}
