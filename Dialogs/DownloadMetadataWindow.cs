using System.IO;
using System.Windows;
using System.Windows.Controls;
using Sink.Controls;
using Sink.Services;
using Sink.Services.Download;

namespace Sink.Dialogs;

/// <summary>
/// The metadata pop-up for a node on the download page — the same idea as
/// <see cref="MetadataWindow"/> for the library, but editing a
/// <see cref="DownloadNode"/> before it is downloaded, cover art included
/// (task 106). Which fields show depends on the node's kind, mirroring the
/// inline grid. Arrow keys walk the fields (task 104).
/// </summary>
public sealed class DownloadMetadataWindow : SinkDialog
{
    private readonly DownloadNode _node;
    private readonly List<Action> _apply = [];
    private readonly TextBlock _artValue;
    private string? _pendingArt;
    private bool _artChanged;

    public bool Applied { get; private set; }

    public DownloadMetadataWindow(DownloadNode node)
    {
        _node = node;
        _pendingArt = node.ArtworkOverride;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        Title = "Edit metadata";

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var editors = new List<Control>();
        var showTitle = node.Kind is DownloadKind.Track or DownloadKind.Single;
        var showArtist = node.Kind is DownloadKind.Single or DownloadKind.Artist
                         || (node.Kind == DownloadKind.Album);
        var showAlbum = node.Kind is DownloadKind.Single or DownloadKind.Album;
        var showGenre = node.Kind is DownloadKind.Single or DownloadKind.Artist or DownloadKind.Album;
        var showTrackNo = node.Kind is DownloadKind.Track or DownloadKind.Single;
        var showArt = node.Kind is DownloadKind.Single or DownloadKind.Album;

        if (showTitle)
        {
            var box = Field();
            box.Text = node.Title;
            editors.Add(box);
            _apply.Add(() => { if (!string.IsNullOrWhiteSpace(box.Text)) node.Title = box.Text.Trim(); });
            AddRow(grid, "Title", box);
        }
        if (showTrackNo)
        {
            var box = Field();
            box.Text = node.TrackNumberText;
            editors.Add(box);
            _apply.Add(() => node.TrackNumberText = box.Text.Trim());
            AddRow(grid, "Track no.", box);
        }
        if (showArtist)
        {
            var box = Auto(SuggestionField.Artist);
            box.Text = node.Artist;
            editors.Add(box);
            _apply.Add(() => { if (!string.IsNullOrWhiteSpace(box.Text)) node.Artist = box.Text.Trim(); });
            AddRow(grid, "Artist", box);
        }
        if (showAlbum)
        {
            var box = Auto(SuggestionField.Album);
            box.Text = node.Album;
            editors.Add(box);
            _apply.Add(() => { if (!string.IsNullOrWhiteSpace(box.Text)) node.Album = box.Text.Trim(); });
            AddRow(grid, "Album", box);
        }
        if (showGenre)
        {
            var box = Auto(SuggestionField.Genre);
            box.Text = node.Genre;
            editors.Add(box);
            _apply.Add(() => { if (!string.IsNullOrWhiteSpace(box.Text)) node.Genre = box.Text.Trim(); });
            AddRow(grid, "Genre", box);
        }

        _artValue = new TextBlock
        {
            Foreground = Hex("#C7CCD6"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (showArt)
        {
            var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            var choose = SmallButton("Choose…", (_, _) => ChooseArt());
            var clear = SmallButton("Clear", (_, _) => { _pendingArt = null; _artChanged = true; RefreshArt(); });
            clear.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(clear, Dock.Right);
            DockPanel.SetDock(choose, Dock.Right);
            row.Children.Add(clear);
            row.Children.Add(choose);
            row.Children.Add(_artValue);
            AddRow(grid, "Cover art", row);
            RefreshArt();
        }

        var subtitle = node.Kind switch
        {
            DownloadKind.Artist => "Artist — changes apply to every album",
            DownloadKind.Album => "Album",
            DownloadKind.Track => "Track",
            _ => "Single",
        };
        Compose("Metadata", string.IsNullOrWhiteSpace(node.Name) ? "Edit details" : node.Name, subtitle, grid,
            new FooterButton("Cancel", false, (_, _) => Close()),
            new FooterButton("Save", true, Save_Click));

        EnableArrowFieldNavigation(editors.ToArray());
        if (editors.Count > 0)
            Loaded += (_, _) => { editors[0].Focus(); (editors[0] as TextBox)?.SelectAll(); };
    }

    private void ChooseArt()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose cover art",
            Filter = "Images|*.jpg;*.jpeg;*.png;*.webp;*.bmp|All files|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        _pendingArt = dialog.FileName;
        _artChanged = true;
        RefreshArt();
    }

    private void RefreshArt() =>
        _artValue.Text = string.IsNullOrWhiteSpace(_pendingArt) ? "None (yt-dlp's thumbnail)" : Path.GetFileName(_pendingArt);

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        foreach (var apply in _apply) apply();
        if (_artChanged) _node.ArtworkOverride = string.IsNullOrWhiteSpace(_pendingArt) ? null : _pendingArt;
        Applied = true;
        DialogResult = true;
    }

    private void AddRow(Grid grid, string label, UIElement editor)
    {
        var r = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var text = new TextBlock
        {
            Text = label, Foreground = Hex("#858C9B"), FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 10, 0, 0),
        };
        Grid.SetRow(text, r);
        Grid.SetColumn(text, 0);
        if (editor is FrameworkElement fe && fe.Margin == default)
            fe.Margin = new Thickness(0, 10, 0, 0);
        Grid.SetRow(editor, r);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(text);
        grid.Children.Add(editor);
    }

    private Button SmallButton(string label, RoutedEventHandler onClick)
    {
        var b = new Button
        {
            Content = label, Padding = new Thickness(11, 6, 11, 6), BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand, Background = Hex("#242A34"), Foreground = Hex("#D1D5DD"),
        };
        b.Click += onClick;
        return b;
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
