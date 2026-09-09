using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    private bool _ipodConnected;
    private bool _ipodSyncing;
    private bool _recordSpinning;
    private IpodDevice? _ipodDevice;
    private readonly DispatcherTimer _ipodPollTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private Point _trackDragStart;
    private const string TrackDragFormat = "Sink.TrackIds";

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
        _ipodPollTimer.Tick += (_, _) => PollForIpod();
        _ipodPollTimer.Start();
        PollForIpod();
    }

    private void PollForIpod()
    {
        var device = IpodService.Detect();
        if (device is not null)
        {
            if (!_ipodConnected || _ipodDevice?.RootPath != device.RootPath)
            {
                _ipodDevice = device;
                SetIpodConnected(true);
            }
            return;
        }
        if (_ipodConnected && _ipodDevice is not null) SetIpodConnected(false);
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

    private void TracksGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var count = TracksGrid.SelectedItems.Count;
        SelectionBar.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
        SelectionText.Text = $"{count} track{(count == 1 ? "" : "s")} selected";
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e) => TracksGrid.UnselectAll();

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
        UpdateRecordSpin();
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
        UpdateRecordSpin();
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

    private void IpodButton_Click(object sender, RoutedEventArgs e)
    {
        if (_ipodConnected) return;
        SetIpodConnected(true);
    }

    private void SetIpodConnected(bool connected)
    {
        _ipodConnected = connected;
        if (connected)
        {
            var summary = _ipodDevice?.Summary ?? "Simulated iPod · 160 GB · 84 GB free";
            var header = _ipodDevice is null ? "Simulated iPod · 160 GB" : $"{_ipodDevice.Name} · {_ipodDevice.CapacityText}";
            RecordLabel.Fill = new SolidColorBrush(Color.FromRgb(40, 91, 184));
            IpodStateText.Text = "IPOD";
            IpodStateText.Foreground = new SolidColorBrush(Color.FromRgb(139, 124, 255));
            IpodButton.ToolTip = summary;
            IpodMenuHeader.Header = header;
        }
        else
        {
            _ipodDevice = null;
            RecordRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            _ipodSyncing = false;
            _recordSpinning = false;
            RecordLabel.Fill = new SolidColorBrush(Color.FromRgb(58, 64, 75));
            IpodStateText.Text = "OFFLINE";
            IpodStateText.Foreground = new SolidColorBrush(Color.FromRgb(154, 161, 175));
            IpodButton.ToolTip = "No iPod connected · Click to simulate connection";
            IpodMenuHeader.Header = "No iPod connected";
        }
        UpdateRecordSpin();
    }

    private void UpdateRecordSpin()
    {
        if (_ipodSyncing) return;
        var shouldSpin = _isPlaying && _nowPlaying is not null;
        if (shouldSpin == _recordSpinning) return;
        _recordSpinning = shouldSpin;
        if (shouldSpin)
        {
            var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(2.6)) { RepeatBehavior = RepeatBehavior.Forever };
            RecordRotation.BeginAnimation(RotateTransform.AngleProperty, spin);
        }
        else
        {
            RecordRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }

    private void IpodMenu_Opened(object sender, RoutedEventArgs e)
    {
        foreach (var item in IpodMenu.Items.OfType<MenuItem>().Skip(1)) item.IsEnabled = _ipodConnected;
    }

    private void SyncIpod_Click(object sender, RoutedEventArgs e) => StartIpodSync();

    private void StartIpodSync()
    {
        if (!_ipodConnected || _ipodSyncing) return;
        _ipodSyncing = true;
        _recordSpinning = false;
        IpodStateText.Text = "SYNCING";
        var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever };
        RecordRotation.BeginAnimation(RotateTransform.AngleProperty, spin);
        var stopTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.2) };
        stopTimer.Tick += (_, _) =>
        {
            stopTimer.Stop();
            RecordRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            _ipodSyncing = false;
            IpodStateText.Text = "IPOD";
            UpdateRecordSpin();
        };
        stopTimer.Start();
    }

    private void EjectIpod_Click(object sender, RoutedEventArgs e) => SetIpodConnected(false);

    private void IpodButton_DragOver(object sender, DragEventArgs e)
    {
        var canSync = _ipodConnected && e.Data.GetDataPresent(TrackDragFormat);
        e.Effects = canSync ? DragDropEffects.Copy : DragDropEffects.None;
        if (canSync) RecordLabel.Fill = new SolidColorBrush(Color.FromRgb(92, 89, 206));
        e.Handled = true;
    }

    private void IpodButton_DragLeave(object sender, DragEventArgs e)
    {
        if (_ipodConnected && !_ipodSyncing) RecordLabel.Fill = new SolidColorBrush(Color.FromRgb(40, 91, 184));
    }

    private void IpodButton_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(TrackDragFormat) is not Guid[] trackIds) return;
        e.Handled = true;
        if (!_ipodConnected)
        {
            PlaybackStatus.Text = "Connect an iPod before syncing";
            return;
        }
        var count = _tracks.Count(track => trackIds.Contains(track.Id) && !track.ExcludedFromShuffle);
        PlaybackStatus.Text = count > 0 ? $"Syncing {count} track{(count == 1 ? "" : "s")} to iPod" : "Nothing to sync";
        if (count > 0) StartIpodSync();
    }

    private void TracksGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _trackDragStart = e.GetPosition(TracksGrid);

    private void TracksGrid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var position = e.GetPosition(TracksGrid);
        if (Math.Abs(position.X - _trackDragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(position.Y - _trackDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var selected = TracksGrid.SelectedItems.Cast<Track>().Select(track => track.Id).ToArray();
        if (selected.Length == 0 && TracksGrid.SelectedItem is Track track) selected = [track.Id];
        if (selected.Length == 0) return;
        var data = new DataObject();
        data.SetData(TrackDragFormat, selected);
        DragDrop.DoDragDrop(TracksGrid, data, DragDropEffects.Copy);
    }

    private void PlaylistItem_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not ListBoxItem item || !e.Data.GetDataPresent(TrackDragFormat)) { e.Effects = DragDropEffects.None; return; }
        item.Tag = "DragOver";
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void PlaylistItem_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is ListBoxItem item) item.Tag = null;
    }

    private void PlaylistItem_Drop(object sender, DragEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: Playlist playlist } item || e.Data.GetData(TrackDragFormat) is not Guid[] trackIds) return;
        item.Tag = null;
        var added = 0;
        foreach (var trackId in trackIds)
        {
            if (playlist.TrackIds.Contains(trackId)) continue;
            playlist.TrackIds.Add(trackId);
            added++;
        }
        PlaylistList.Items.Refresh();
        PlaybackStatus.Text = added > 0 ? $"Added {added} track{(added == 1 ? "" : "s")} to {playlist.Name}" : $"Already in {playlist.Name}";
        e.Handled = true;
    }

    private void Duplicates_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new DuplicateWindow(_tracks) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedIds.Count == 0) return;
        var ids = dialog.SelectedIds;
        foreach (var track in _tracks.Where(track => ids.Contains(track.Id)).ToList()) _tracks.Remove(track);
        foreach (var playlist in _playlists)
            foreach (var id in playlist.TrackIds.Where(ids.Contains).ToList()) playlist.TrackIds.Remove(id);
        if (_nowPlaying is not null && ids.Contains(_nowPlaying.Id))
        {
            _mediaPlayer.Stop(); _mediaPlayer.Close(); _nowPlaying = null; _isPlaying = false; _playbackTimer.Stop();
            PlayerTitle.Text = "Choose something to play"; PlayerArtist.Text = "Your library is ready"; PlayerArtInitial.Text = "♫"; PlayPauseButton.Content = "▶";
            UpdateRecordSpin();
        }
        PlaylistList.Items.Refresh();
        RenderLibrary();
        PlaybackStatus.Text = $"Removed {ids.Count} duplicate{(ids.Count == 1 ? "" : "s")}";
    }

    private static bool HasFileDrop(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop);

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        ImportOverlay.Visibility = HasFileDrop(e) ? Visibility.Visible : Visibility.Collapsed;
        e.Effects = HasFileDrop(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasFileDrop(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e) => ImportOverlay.Visibility = Visibility.Collapsed;

    private void Window_Drop(object sender, DragEventArgs e)
    {
        ImportOverlay.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        var imported = MusicImporter.Import(paths);
        foreach (var track in imported) _tracks.Add(track);
        RenderLibrary();
        PlaybackStatus.Text = imported.Count > 0 ? $"Imported {imported.Count} track{(imported.Count == 1 ? "" : "s")}" : "No supported audio files found";
        e.Handled = true;
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
