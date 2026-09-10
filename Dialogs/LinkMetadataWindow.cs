using System.Windows;
using System.Windows.Controls;
using Sink.Services.Download;

namespace Sink.Dialogs;

/// <summary>
/// Bulk-edits artist / album / genre across one or more pending download rows.
/// A field left showing the "mixed" placeholder — or left blank — is not written
/// back, so untouched values on each row survive.
/// </summary>
public sealed class LinkMetadataWindow : SinkDialog
{
    private const string Mixed = "— mixed —";
    private readonly IReadOnlyList<DownloadItem> _items;
    private readonly TextBox _artist = Field();
    private readonly TextBox _album = Field();
    private readonly TextBox _genre = Field();

    public LinkMetadataWindow(IReadOnlyList<DownloadItem> items)
    {
        _items = items;
        Width = 420;
        Height = 340;
        Title = "Edit download details";

        _artist.Text = Shared(i => i.Artist);
        _album.Text = Shared(i => i.Album);
        _genre.Text = Shared(i => i.Genre);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var rows = new[] { ("Artist", _artist), ("Album", _album), ("Genre", _genre) };
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

        var subtitle = items.Count == 1 ? "1 link" : $"{items.Count} links";
        Compose("Metadata", "Edit details", subtitle, grid,
            new FooterButton("Cancel", false, (_, _) => Close()),
            new FooterButton("Apply", true, Apply_Click));
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items)
        {
            if (Changed(_artist)) item.Artist = _artist.Text.Trim();
            if (Changed(_album)) item.Album = _album.Text.Trim();
            if (Changed(_genre)) item.Genre = _genre.Text.Trim();
        }
        DialogResult = true;
    }

    private bool Changed(TextBox box) => box.Text != Mixed && !string.IsNullOrWhiteSpace(box.Text);

    private string Shared(Func<DownloadItem, string> selector)
    {
        var values = _items.Select(selector).Distinct().ToList();
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
