using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Sink.Controls;
using Sink.Models;
using Sink.Services;

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
    private readonly TextBox _artist = Auto(SuggestionField.Artist);
    private readonly TextBox _album = Auto(SuggestionField.Album);
    private readonly TextBox _genre = Auto(SuggestionField.Genre);
    private readonly TextBox _year = Field();
    private readonly Image _artPreview = new() { Width = 44, Height = 44, Stretch = Stretch.UniformToFill, ClipToBounds = true };
    private string? _pendingArtPath;

    public bool Applied { get; private set; }

    public MetadataWindow(IReadOnlyList<Track> tracks)
    {
        _tracks = tracks;
        Width = 420;
        Height = 540;
        Title = "Edit metadata";

        _title.Text = Shared(t => t.Title);
        _artist.Text = Shared(t => t.Artist);
        _album.Text = Shared(t => t.Album);
        _genre.Text = Shared(t => t.Genre);
        _year.Text = Shared(t => t.Year > 0 ? t.Year.ToString() : "");
        _pendingArtPath = tracks.Select(t => t.ArtworkPath).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
        RefreshArtPreview();

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

        var artRow = rows.Length;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var artLabel = new TextBlock
        {
            Text = "Album art", Foreground = Hex("#858C9B"), FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 14, 0, 0)
        };
        Grid.SetRow(artLabel, artRow);
        Grid.SetColumn(artLabel, 0);
        var artPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        var artBorder = new Border
        {
            Width = 44, Height = 44, CornerRadius = new CornerRadius(6), Background = Hex("#0F1218"),
            BorderBrush = Hex("#353C49"), BorderThickness = new Thickness(1), ClipToBounds = true, Child = _artPreview
        };
        var uploadButton = new Button
        {
            Content = "Upload…", Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(12, 7, 12, 7),
            BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
            Background = Hex("#242A34"), Foreground = Hex("#D1D5DD"), VerticalAlignment = VerticalAlignment.Center
        };
        uploadButton.Click += UploadArt_Click;
        artPanel.Children.Add(artBorder);
        artPanel.Children.Add(uploadButton);
        Grid.SetRow(artPanel, artRow);
        Grid.SetColumn(artPanel, 1);
        grid.Children.Add(artLabel);
        grid.Children.Add(artPanel);

        var subtitle = tracks.Count == 1 ? tracks[0].FileName : $"{tracks.Count} tracks selected";
        Compose("Metadata", "Edit details", subtitle, grid,
            new FooterButton("Cancel", false, (_, _) => Close()),
            new FooterButton("Save", true, Save_Click));

        EnableArrowFieldNavigation(_title, _artist, _album, _genre, _year);
        Loaded += (_, _) => { _title.Focus(); _title.SelectAll(); };
    }

    /// <summary>Picks an image file and applies it to every track being edited immediately — the same "one album, one cover" model CropAlbumArt/DownloadAlbumArt already use, just from a manual file instead of the embedded tag or a web search.</summary>
    private void UploadArt_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose album art", Filter = "Image files|*.jpg;*.jpeg;*.png;*.bmp;*.gif|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;

        byte[] data;
        try { data = File.ReadAllBytes(dialog.FileName); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }

        var artist = Changed(_artist) ? _artist.Text.Trim() : _tracks[0].Artist;
        var album = Changed(_album) ? _album.Text.Trim() : _tracks[0].Album;
        var saved = Artwork.SaveOverride($"{album}|{artist}", data);
        if (saved is null) return;
        _pendingArtPath = saved;
        RefreshArtPreview();
    }

    private void RefreshArtPreview()
    {
        if (_pendingArtPath is null || !File.Exists(_pendingArtPath)) { _artPreview.Source = null; return; }
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(_pendingArtPath, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        _artPreview.Source = bitmap;
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
            if (_pendingArtPath is not null) track.ArtworkPath = _pendingArtPath;
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

    private static TextBox Field() => Styled(new TextBox());

    private static TextBox Auto(SuggestionField field) => Styled(new AutoCompleteTextBox { SuggestionField = field });

    private static TextBox Styled(TextBox box)
    {
        box.Height = 34;
        box.Padding = new Thickness(10, 7, 10, 7);
        box.Foreground = Hex("#F4F6FA");
        box.Background = Hex("#0F1218");
        box.BorderBrush = Hex("#353C49");
        box.CaretBrush = Hex("#F4F6FA");
        box.VerticalContentAlignment = VerticalAlignment.Center;
        return box;
    }
}
