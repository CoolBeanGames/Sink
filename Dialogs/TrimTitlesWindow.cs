using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Sink.Services.Download;

namespace Sink.Dialogs;

/// <summary>
/// Bulk-cleans an album's downloaded track titles: trim a fixed number of
/// characters off either end, cut everything before/after a chosen character,
/// and optionally pull a leading number out into the track-number field.
/// A live before → after list previews every track before you apply.
/// </summary>
public sealed class TrimTitlesWindow : SinkDialog
{
    private readonly IReadOnlyList<DownloadNode> _tracks;

    private readonly CheckBox _removeFirst = Check("Remove first");
    private readonly TextBox _removeFirstN = Num();
    private readonly CheckBox _removeLast = Check("Remove last");
    private readonly TextBox _removeLastN = Num();
    private readonly CheckBox _upTo = Check("Cut up to & including");
    private readonly TextBox _upToChar = Char();
    private readonly CheckBox _after = Check("Cut everything after");
    private readonly TextBox _afterChar = Char();
    private readonly CheckBox _extractNumber = Check("Use leading number as track number");
    private readonly ListBox _preview = new()
    {
        Height = 190,
        Background = Hex("#0F1218"),
        BorderBrush = Hex("#2A303C"),
        Foreground = Hex("#C7CCD6"),
        FontSize = 11,
    };

    public TrimTitlesWindow(IReadOnlyList<DownloadNode> tracks)
    {
        _tracks = tracks;
        Width = 520;
        Height = 520;
        Title = "Trim track titles";

        var body = new StackPanel();
        body.Children.Add(Row(_removeFirst, _removeFirstN, "characters"));
        body.Children.Add(Row(_removeLast, _removeLastN, "characters"));
        body.Children.Add(Row(_upTo, _upToChar, "e.g.  -"));
        body.Children.Add(Row(_after, _afterChar, "e.g.  ("));
        _extractNumber.Margin = new Thickness(0, 6, 0, 0);
        body.Children.Add(_extractNumber);
        body.Children.Add(new TextBlock
        {
            Text = "Preview", Foreground = Hex("#858C9B"), FontSize = 10,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 5)
        });
        body.Children.Add(_preview);

        foreach (var box in new[] { _removeFirst, _removeLast, _upTo, _after, _extractNumber })
            box.Click += (_, _) => Refresh();
        foreach (var tb in new[] { _removeFirstN, _removeLastN, _upToChar, _afterChar })
            tb.TextChanged += (_, _) => Refresh();

        Compose("Metadata", "Trim titles", $"{tracks.Count} track{(tracks.Count == 1 ? "" : "s")}", body,
            new FooterButton("Cancel", false, (_, _) => Close()),
            new FooterButton("Apply", true, Apply_Click));

        Refresh();
    }

    private readonly record struct Spec(int First, int Last, char? Up, char? After, bool Number);

    private Spec ReadSpec() => new(
        _removeFirst.IsChecked == true && int.TryParse(_removeFirstN.Text, out var f) ? Math.Max(0, f) : 0,
        _removeLast.IsChecked == true && int.TryParse(_removeLastN.Text, out var l) ? Math.Max(0, l) : 0,
        _upTo.IsChecked == true && _upToChar.Text.Length > 0 ? _upToChar.Text[0] : null,
        _after.IsChecked == true && _afterChar.Text.Length > 0 ? _afterChar.Text[0] : null,
        _extractNumber.IsChecked == true);

    private static (string title, int track) Transform(string original, Spec s)
    {
        var t = original;
        if (s.Up is { } u) { var i = t.IndexOf(u); if (i >= 0) t = t[(i + 1)..]; }
        if (s.After is { } a) { var i = t.IndexOf(a); if (i >= 0) t = t[..i]; }
        if (s.First > 0) t = s.First < t.Length ? t[s.First..] : "";
        if (s.Last > 0) t = s.Last < t.Length ? t[..^s.Last] : "";
        t = t.Trim().Trim('-', '–', '—', '.', ':', '·').Trim();

        var track = 0;
        if (s.Number)
        {
            var m = Regex.Match(original, @"\d+");
            if (m.Success) int.TryParse(m.Value, out track);
        }
        return (t, track);
    }

    private void Refresh()
    {
        var spec = ReadSpec();
        _preview.Items.Clear();
        foreach (var node in _tracks)
        {
            var (title, track) = Transform(node.Title, spec);
            var suffix = spec.Number && track > 0 ? $"   (#{track})" : "";
            _preview.Items.Add($"{node.Title}   →   {title}{suffix}");
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var spec = ReadSpec();
        foreach (var node in _tracks)
        {
            var (title, track) = Transform(node.Title, spec);
            if (!string.IsNullOrWhiteSpace(title)) node.Title = title;
            if (spec.Number && track > 0) node.TrackNumber = track;
        }
        DialogResult = true;
    }

    private static CheckBox Check(string text) => new()
    {
        Content = text, Foreground = Hex("#C7CCD6"), VerticalAlignment = VerticalAlignment.Center, Width = 190
    };

    private static TextBox Num() => new()
    {
        Width = 54, Text = "1", Padding = new Thickness(6, 4, 6, 4),
        Foreground = Hex("#F4F6FA"), Background = Hex("#0F1218"), BorderBrush = Hex("#353C49"),
        CaretBrush = Hex("#F4F6FA"), VerticalContentAlignment = VerticalAlignment.Center
    };

    private static TextBox Char() => new()
    {
        Width = 54, MaxLength = 1, Padding = new Thickness(6, 4, 6, 4),
        Foreground = Hex("#F4F6FA"), Background = Hex("#0F1218"), BorderBrush = Hex("#353C49"),
        CaretBrush = Hex("#F4F6FA"), VerticalContentAlignment = VerticalAlignment.Center
    };

    private static Panel Row(CheckBox box, TextBox field, string hint)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        panel.Children.Add(box);
        panel.Children.Add(field);
        panel.Children.Add(new TextBlock
        {
            Text = "  " + hint, Foreground = Hex("#6E7584"), FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0)
        });
        return panel;
    }
}
