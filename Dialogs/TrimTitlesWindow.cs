using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Sink.Models;

namespace Sink.Dialogs;

/// <summary>
/// Bulk-cleans a batch of track titles: trim a fixed number of characters
/// off either end, cut everything before/after a chosen character, and
/// optionally pull a leading number out into the track-number field. A live
/// before → after list previews every track before you apply. Works on
/// anything implementing <see cref="ITitleTrimmable"/> — the download tree
/// and the Tags page's library tracks alike (task 159).
/// </summary>
public sealed class TrimTitlesWindow : SinkDialog
{
    private readonly IReadOnlyList<ITitleTrimmable> _tracks;

    private readonly CheckBox _replace = Check("Replace");
    private readonly TextBox _replaceFind = Text(140);
    private readonly TextBox _replaceWith = Text(140);
    private readonly CheckBox _cutAll = Check("Remove every instance of");
    private readonly TextBox _cutAllText = Text();
    private readonly CheckBox _removeFirst = Check("Remove first");
    private readonly TextBox _removeFirstN = Num();
    private readonly CheckBox _removeLast = Check("Remove last");
    private readonly TextBox _removeLastN = Num();
    private readonly CheckBox _upTo = Check("Cut up to & including");
    private readonly TextBox _upToText = Text();
    private readonly CheckBox _after = Check("Cut everything after");
    private readonly TextBox _afterText = Text();
    private readonly CheckBox _extractNumber = Check("Use leading number as track number");
    private readonly ListBox _preview = new()
    {
        Height = 190,
        Background = Hex("#0F1218"),
        BorderBrush = Hex("#2A303C"),
        Foreground = Hex("#C7CCD6"),
        FontSize = 11,
    };

    public TrimTitlesWindow(IReadOnlyList<ITitleTrimmable> tracks)
    {
        _tracks = tracks;
        Width = 540;
        Height = 720;
        Title = "Trim track titles";

        var body = new StackPanel();
        body.Children.Add(ReplaceRow(_replace, _replaceFind, _replaceWith));
        body.Children.Add(Row(_cutAll, _cutAllText, "e.g.  (Official Video)"));
        body.Children.Add(Row(_removeFirst, _removeFirstN, "characters"));
        body.Children.Add(Row(_removeLast, _removeLastN, "characters"));
        body.Children.Add(Row(_upTo, _upToText, "e.g.  Artist -"));
        body.Children.Add(Row(_after, _afterText, "e.g.  (feat."));
        _extractNumber.Margin = new Thickness(0, 6, 0, 0);
        body.Children.Add(_extractNumber);
        body.Children.Add(new TextBlock
        {
            Text = "Preview", Foreground = Hex("#858C9B"), FontSize = 10,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 5)
        });
        body.Children.Add(_preview);

        foreach (var box in new[] { _replace, _cutAll, _removeFirst, _removeLast, _upTo, _after, _extractNumber })
            box.Click += (_, _) => Refresh();
        foreach (var tb in new[] { _replaceFind, _replaceWith, _cutAllText, _removeFirstN, _removeLastN, _upToText, _afterText })
            tb.TextChanged += (_, _) => Refresh();

        Compose("Metadata", "Trim titles", $"{tracks.Count} track{(tracks.Count == 1 ? "" : "s")}", body,
            new FooterButton("Cancel", false, (_, _) => Close()),
            new FooterButton("Apply", true, Apply_Click));

        Refresh();
    }

    private readonly record struct Spec(
        string? ReplaceFind, string? ReplaceWith, string? CutAll,
        int First, int Last, string? Up, string? After, bool Number);

    private Spec ReadSpec() => new(
        _replace.IsChecked == true && _replaceFind.Text.Length > 0 ? _replaceFind.Text : null,
        _replaceWith.Text,
        _cutAll.IsChecked == true && _cutAllText.Text.Length > 0 ? _cutAllText.Text : null,
        _removeFirst.IsChecked == true && int.TryParse(_removeFirstN.Text, out var f) ? Math.Max(0, f) : 0,
        _removeLast.IsChecked == true && int.TryParse(_removeLastN.Text, out var l) ? Math.Max(0, l) : 0,
        _upTo.IsChecked == true && _upToText.Text.Length > 0 ? _upToText.Text : null,
        _after.IsChecked == true && _afterText.Text.Length > 0 ? _afterText.Text : null,
        _extractNumber.IsChecked == true);

    private static (string title, int track) Transform(string original, Spec s)
    {
        var t = original;
        if (s.ReplaceFind is { Length: > 0 } find) t = t.Replace(find, s.ReplaceWith ?? "", StringComparison.OrdinalIgnoreCase);
        if (s.CutAll is { Length: > 0 } cut) t = t.Replace(cut, "", StringComparison.OrdinalIgnoreCase);
        if (s.Up is { Length: > 0 } u) { var i = t.IndexOf(u, StringComparison.OrdinalIgnoreCase); if (i >= 0) t = t[(i + u.Length)..]; }
        if (s.After is { Length: > 0 } a) { var i = t.IndexOf(a, StringComparison.OrdinalIgnoreCase); if (i >= 0) t = t[..i]; }
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

    private static TextBox Text(double width = 90) => new()
    {
        Width = width, Padding = new Thickness(6, 4, 6, 4),
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

    private static Panel ReplaceRow(CheckBox box, TextBox find, TextBox replaceWith)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        panel.Children.Add(box);
        panel.Children.Add(find);
        panel.Children.Add(new TextBlock
        {
            Text = "  with  ", Foreground = Hex("#6E7584"), FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(replaceWith);
        return panel;
    }
}
