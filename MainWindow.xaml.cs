using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Sink.Models;
using Sink.Services;

namespace Sink;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<Track> _tracks = new(SeedLibrary.Create());
    private readonly ObservableCollection<Playlist> _playlists = [];
    private LibraryCategory _category = LibraryCategory.Albums;
    private string? _drilldown;
    private Playlist? _activePlaylist;

    public MainWindow()
    {
        InitializeComponent();
        _playlists.Add(new Playlist { Name = "Favorites" });
        foreach (var track in _tracks.Where((_, index) => index % 2 == 0).Take(3)) _playlists[0].TrackIds.Add(track.Id);
        PlaylistList.ItemsSource = _playlists;
        RenderLibrary();
    }

    private void Category_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !Enum.TryParse(button.Name.Replace("Button", ""), out LibraryCategory category)) return;
        _category = category; _drilldown = null; _activePlaylist = null; PlaylistList.SelectedItem = null;
        SetActiveNavigation(button);
        RenderLibrary();
    }

    private void SetActiveNavigation(Button? active)
    {
        foreach (var button in new[] { ArtistsButton, AlbumsButton, GenresButton, SongsButton }) button.Tag = button == active ? "Active" : button.Name.Replace("Button", "");
    }

    private void RenderLibrary()
    {
        var query = SearchBox?.Text?.Trim() ?? "";
        IEnumerable<Track> visible = _tracks;
        if (_activePlaylist is not null) visible = visible.Where(track => _activePlaylist.TrackIds.Contains(track.Id));
        if (_drilldown is not null) visible = _category switch
        {
            LibraryCategory.Albums => visible.Where(track => track.Album == _drilldown),
            LibraryCategory.Artists => visible.Where(track => track.Artist == _drilldown),
            LibraryCategory.Genres => visible.Where(track => track.Genre == _drilldown),
            _ => visible
        };
        if (query.Length > 0) visible = visible.Where(track => $"{track.Title} {track.Artist} {track.Album} {track.Genre}".Contains(query, StringComparison.OrdinalIgnoreCase));

        var showTracks = _category is LibraryCategory.Songs or LibraryCategory.Playlist || _drilldown is not null;
        GroupsScroller.Visibility = showTracks ? Visibility.Collapsed : Visibility.Visible;
        TracksBorder.Visibility = showTracks ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = _drilldown is null ? Visibility.Collapsed : Visibility.Visible;

        if (showTracks)
        {
            var rows = visible.OrderBy(track => track.Album).ThenBy(track => track.TrackNumber).ToList();
            TracksGrid.ItemsSource = rows;
            ViewTitle.Text = _drilldown ?? _activePlaylist?.Name ?? "Songs";
            ViewSubtitle.Text = $"{rows.Count} tracks";
            return;
        }

        var groups = _category switch
        {
            LibraryCategory.Artists => _tracks.GroupBy(track => track.Artist).Select(group => Card(group.Key, $"{group.Count()} tracks", group.Key)),
            LibraryCategory.Genres => _tracks.GroupBy(track => track.Genre).Select(group => Card(group.Key, $"{group.Count()} tracks", group.Key)),
            _ => _tracks.GroupBy(track => track.Album).Select(group => Card(group.Key, group.First().Artist, group.Key))
        };
        var cards = groups.Where(card => query.Length == 0 || card.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).OrderBy(card => card.Name).ToList();
        GroupsView.ItemsSource = cards;
        ViewTitle.Text = _category.ToString();
        ViewSubtitle.Text = $"{cards.Count} {_category.ToString().ToLowerInvariant()}";
    }

    private static GroupCard Card(string name, string detail, string colorSeed)
    {
        var palette = new[] { "#273A78", "#6E354B", "#285D56", "#6C4D31" };
        return new GroupCard(name, detail, name[..1].ToUpperInvariant(), new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette[Math.Abs(colorSeed.GetHashCode()) % palette.Length])));
    }

    private void GroupCard_Click(object sender, RoutedEventArgs e) { }

    private void GroupCard_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button { Tag: string name }) return;
        _drilldown = name;
        RenderLibrary();
        e.Handled = true;
    }

    private void TracksGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TracksGrid.SelectedItem is Track track) PlayTrack(track);
    }

    private void PlayTrack(Track track) => PlaybackStatus.Text = $"▶  Playing {track.Title} — {track.Artist}";

    private void BackButton_Click(object sender, RoutedEventArgs e) { _drilldown = null; RenderLibrary(); }
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { if (IsLoaded) RenderLibrary(); }

    private void NewPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TextPromptWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var playlist = new Playlist { Name = dialog.Answer };
        _playlists.Add(playlist);
        PlaylistList.Items.Refresh();
        PlaylistList.SelectedItem = playlist;
    }

    private void PlaylistList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlaylistList.SelectedItem is not Playlist playlist) return;
        _activePlaylist = playlist; _category = LibraryCategory.Playlist; _drilldown = null; SetActiveNavigation(null); RenderLibrary();
    }

    private sealed record GroupCard(string Name, string Detail, string Initial, Brush Color);
}
