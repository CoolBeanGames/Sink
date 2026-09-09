using System.Windows;
using System.Windows.Controls;
using Sink.Models;

namespace Sink.Dialogs;

/// <summary>
/// Edits shared metadata for one or more tracks. Fields left showing the
/// "multiple values" placeholder are not written back.
/// </summary>
public sealed class MetadataWindow : SinkDialog
{
    private const string Mixed = "— multiple —";
    private readonly IReadOnlyList<Track> _tracks;
    private readonly TextBox _title = Field();
    private readonly TextBox _artist = Field();
    private readonly TextBox _album = Field();
    private readonly TextBox _genre = Field();
    private readonly TextBox _year = Field();

    public bool Applied { get; private set; }

    public MetadataWindow(IReadOnlyList<Track> tracks)
    {
        _tracks = tracks;
        Width = 420;
        Height = 486;
        Title = "Edit metadata";

        _title.Text = Shared(t => t.Title);
        _artist.Text = Shared(t => t.Artist);
        _album.Text = Shared(t => t.Album);
        _genre.Text = Shared(t => t.Genre);
        _year.Text = Shared(t => t.Year > 0 ? t.Year.ToString() : "");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var rows = new[] { ("Title", _title), ("Artist", _artist), ("Album", _album), ("Genre", _genre), ("Year", _year) };
        for (var i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock
            {
                Text = rows[i].Item1, Foreground = Hex("#858C9B"), FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 10, 0, 0)
            };
            Grid.SetRow(label, i);
            Grid.SetColumn(label, 0);
            var box = rows[i].Item2;
            box.Margin = new Thickness(0, 10, 0, 0);
            Grid.SetRow(box, i);
            Grid.SetColumn(box, 1);
            grid.Children.Add(label);
            grid.Children.Add(box);
        }

        var subtitle = tracks.Count == 1 ? tracks[0].FileName : $"{tracks.Count} tracks selected";
        Compose("Metadata", "Edit details", subtitle, grid,
            new FooterButton("Cancel", false, (_, _) => Close()),
            new FooterButton("Save", true, Save_Click));
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        foreach (var track in _tracks)
        {
            if (Changed(_title)) track.Title = _title.Text.Trim();
            if (Changed(_artist)) track.Artist = _artist.Text.Trim();
            if (Changed(_album)) track.Album = _album.Text.Trim();
            if (Changed(_genre)) track.Genre = _genre.Text.Trim();
            if (Changed(_year) && int.TryParse(_year.Text.Trim(), out var year)) track.Year = year;
        }
        Applied = true;
        DialogResult = true;
    }

    private bool Changed(TextBox box) => box.Text != Mixed && !string.IsNullOrWhiteSpace(box.Text);

    private string Shared(Func<Track, string> selector)
    {
        var values = _tracks.Select(selector).Distinct().ToList();
        return values.Count == 1 ? values[0] : Mixed;
    }

    private static TextBox Field() => new()
    {
        Height = 34, Padding = new Thickness(10, 7, 10, 7),
        Foreground = Hex("#F4F6FA"), Background = Hex("#0F1218"),
        BorderBrush = Hex("#353C49"), CaretBrush = Hex("#F4F6FA"),
        VerticalContentAlignment = VerticalAlignment.Center
    };
}
