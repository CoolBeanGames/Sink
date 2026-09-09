using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
    private readonly MediaPlayer _mediaPlayer = new();
    private readonly DispatcherTimer _playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private Track? _nowPlaying;
    private TimeSpan _simulatedPosition;
    private bool _isPlaying;
    private bool _updatingProgress;

    public MainWindow()
    {
        InitializeComponent();
        _playlists.Add(new Playlist { Name = "Favorites" });
        foreach (var track in _tracks.Where((_, index) => index % 2 == 0).Take(3)) _playlists[0].TrackIds.Add(track.Id);
        PlaylistList.ItemsSource = _playlists;
        _playbackTimer.Tick += PlaybackTimer_Tick;
        _mediaPlayer.MediaOpened += (_, _) => UpdatePlayerDuration();
        _mediaPlayer.MediaEnded += (_, _) => NextTrack();
        _mediaPlayer.Volume = 0.7;
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

    private void PlayTrack(Track track)
    {
        _mediaPlayer.Stop();
        _mediaPlayer.Close();
        _nowPlaying = track;
        _simulatedPosition = TimeSpan.Zero;
        _isPlaying = true;
        var filePath = track.FilePath;
        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            _mediaPlayer.Open(new Uri(filePath));
            _mediaPlayer.Play();
        }
        PlayerTitle.Text = track.Title;
        PlayerArtist.Text = track.Artist;
        PlayerArtInitial.Text = track.Album[..1].ToUpperInvariant();
        PlaybackStatus.Text = $"▶  Playing {track.Title} — {track.Artist}";
        PlayPauseButton.Content = "Ⅱ";
        UpdatePlayerDuration();
        _playbackTimer.Start();
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (_nowPlaying is null || !_isPlaying) return;
        if (!string.IsNullOrWhiteSpace(_nowPlaying.FilePath) && _mediaPlayer.NaturalDuration.HasTimeSpan)
            _simulatedPosition = _mediaPlayer.Position;
        else
        {
            _simulatedPosition += _playbackTimer.Interval;
            if (_simulatedPosition >= _nowPlaying.Duration) NextTrack();
        }
        UpdatePlayerProgress();
    }

    private void UpdatePlayerDuration()
    {
        if (_nowPlaying is null) return;
        var duration = _mediaPlayer.NaturalDuration.HasTimeSpan ? _mediaPlayer.NaturalDuration.TimeSpan : _nowPlaying.Duration;
        ProgressSlider.Maximum = Math.Max(1, duration.TotalSeconds);
        UpdatePlayerProgress();
    }

    private void UpdatePlayerProgress()
    {
        if (_nowPlaying is null) return;
        var duration = TimeSpan.FromSeconds(ProgressSlider.Maximum);
        _updatingProgress = true;
        ProgressSlider.Value = Math.Min(ProgressSlider.Maximum, _simulatedPosition.TotalSeconds);
        _updatingProgress = false;
        ElapsedText.Text = FormatTime(_simulatedPosition);
        RemainingText.Text = $"-{FormatTime(duration - _simulatedPosition)}";
    }

    private static string FormatTime(TimeSpan time) => $"{Math.Max(0, (int)time.TotalMinutes)}:{Math.Max(0, time.Seconds):00}";

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_nowPlaying is null) return;
        _isPlaying = !_isPlaying;
        if (_isPlaying) _mediaPlayer.Play(); else _mediaPlayer.Pause();
        PlayPauseButton.Content = _isPlaying ? "Ⅱ" : "▶";
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => Skip(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => NextTrack();
    private void NextTrack() => Skip(1);

    private void Skip(int direction)
    {
        if (_nowPlaying is null || _tracks.Count == 0) return;
        var index = _tracks.IndexOf(_nowPlaying);
        PlayTrack(_tracks[(index + direction + _tracks.Count) % _tracks.Count]);
    }

    private void ProgressSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingProgress || _nowPlaying is null) return;
        _simulatedPosition = TimeSpan.FromSeconds(e.NewValue);
        if (!string.IsNullOrWhiteSpace(_nowPlaying.FilePath)) _mediaPlayer.Position = _simulatedPosition;
        UpdatePlayerProgress();
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_mediaPlayer is not null) _mediaPlayer.Volume = e.NewValue;
    }

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
