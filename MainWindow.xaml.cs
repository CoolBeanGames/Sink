using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Sink.Dialogs;
using Sink.Models;
using Sink.Services;

namespace Sink;

public enum LibrarySource { Music, Ipod }

public partial class MainWindow : Window
{
    private readonly ObservableCollection<Track> _tracks = [];
    private readonly ObservableCollection<Playlist> _playlists = [];
    private readonly HashSet<Guid> _syncedTrackIds = [];
    private readonly List<Track> _ipodTracks = [];
    private Sink.Services.Ipod.IpodLibrary? _ipodLibrary;
    private string? _ipodLibraryRoot;
    private LibraryCategory _category = LibraryCategory.Albums;
    private LibrarySource _source = LibrarySource.Music;
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
    private readonly DispatcherTimer _ipodPollTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private Point _trackDragStart;
    private const string TrackDragFormat = "Sink.TrackIds";

    public MainWindow()
    {
        InitializeComponent();
        LoadLibrary();
        PlaylistList.ItemsSource = _playlists;
        _playbackTimer.Tick += PlaybackTimer_Tick;
        _mediaPlayer.MediaOpened += (_, _) => UpdatePlayerDuration();
        _mediaPlayer.MediaEnded += (_, _) => NextTrack();
        _mediaPlayer.Volume = 0.7;
        RenderLibrary();
        InitDownloadPage();
        InitPodcasts();
        _ipodPollTimer.Tick += (_, _) => PollForIpod();
        _ipodPollTimer.Start();
        Loaded += (_, _) => PollForIpod();

        PreviewMouseDown += MiddleDragPan_Down;
        PreviewMouseMove += MiddleDragPan_Move;
        PreviewMouseUp += MiddleDragPan_Up;
        LostMouseCapture += (_, _) => _panScrollViewer = null;
    }

    // ---- Middle-mouse drag to scroll (task 98) -------------------------

    private ScrollViewer? _panScrollViewer;
    private Point _panOrigin;
    private double _panOffsetV, _panOffsetH;

    private void MiddleDragPan_Down(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        var sv = FindScrollViewer(e.OriginalSource as DependencyObject);
        if (sv is null || (sv.ScrollableHeight == 0 && sv.ScrollableWidth == 0)) return;
        _panScrollViewer = sv;
        _panOrigin = e.GetPosition(this);
        _panOffsetV = sv.VerticalOffset;
        _panOffsetH = sv.HorizontalOffset;
        Mouse.Capture(this);
        Cursor = Cursors.ScrollAll;
        e.Handled = true;
    }

    private void MiddleDragPan_Move(object sender, MouseEventArgs e)
    {
        if (_panScrollViewer is null) return;
        var p = e.GetPosition(this);
        _panScrollViewer.ScrollToVerticalOffset(_panOffsetV + (p.Y - _panOrigin.Y) * 1.6);
        if (_panScrollViewer.ScrollableWidth > 0)
            _panScrollViewer.ScrollToHorizontalOffset(_panOffsetH + (p.X - _panOrigin.X) * 1.6);
    }

    private void MiddleDragPan_Up(object sender, MouseButtonEventArgs e)
    {
        if (_panScrollViewer is null || e.ChangedButton != MouseButton.Middle) return;
        _panScrollViewer = null;
        Mouse.Capture(null);
        Cursor = Cursors.Arrow;
        e.Handled = true;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is ScrollViewer sv) return sv;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    private void LoadLibrary()
    {
        var data = LibraryStore.Load();
        if (data is null)
        {
            // First run only. Once a library.json exists we honour it even when
            // empty — the user may have deleted every seeded placeholder track.
            foreach (var track in SeedLibrary.Create()) _tracks.Add(track);
            var favorites = new Playlist { Name = "Favorites" };
            foreach (var track in _tracks.Where((_, index) => index % 2 == 0).Take(3)) favorites.TrackIds.Add(track.Id);
            _playlists.Add(favorites);
            SaveLibrary();
            return;
        }
        foreach (var track in data.Tracks) _tracks.Add(track);
        foreach (var playlist in data.Playlists)
        {
            var restored = new Playlist { Name = playlist.Name, Id = playlist.Id };
            foreach (var id in playlist.TrackIds) restored.TrackIds.Add(id);
            _playlists.Add(restored);
        }
        var known = _tracks.Select(t => t.Id).ToHashSet();
        foreach (var id in data.SyncedTrackIds.Where(known.Contains)) _syncedTrackIds.Add(id);
    }

    private void SaveLibrary() => LibraryStore.Save(_tracks, _playlists, _syncedTrackIds);

    private bool _ipodPolling;
    private bool _manualRescan;

    private async void PollForIpod(bool manual = false)
    {
        _manualRescan |= manual;
        if (_ipodPolling) return;
        _ipodPolling = true;
        IpodDevice? device;
        try { device = await Task.Run(IpodService.Detect); }
        finally { _ipodPolling = false; }

        var wasManual = _manualRescan;
        _manualRescan = false;

        if (device is not null)
        {
            var isNew = !_ipodConnected || _ipodDevice?.Key != device.Key;
            var changed = isNew || _ipodDevice?.Tooltip != device.Tooltip;
            if (changed)
            {
                _ipodDevice = device;
                SetIpodConnected(true);
            }
            if (device.LibraryRoot != _ipodLibraryRoot) LoadIpodLibrary(device.LibraryRoot);
            if (isNew)
            {
                Services.Log.Info($"iPod connected: {device.Name} ({device.LibraryRoot ?? "no library root"})");
                PlaybackStatus.Text = $"Connected {device.Name}";
                if (Services.AppSettings.Current.SyncOnConnect && !_ipodWriting)
                    SyncTracksToDevice(_tracks.Where(t => !t.ExcludedFromShuffle).ToList());
            }
            else if (wasManual) PlaybackStatus.Text = $"{device.Name} is connected";
            return;
        }

        if (_ipodConnected && _ipodDevice is not null) SetIpodConnected(false);
        if (_ipodLibraryRoot is not null) LoadIpodLibrary(null);
        if (wasManual) PlaybackStatus.Text = "No iPod found — check the cable, or click the record to simulate one";
    }

    private async void LoadIpodLibrary(string? root)
    {
        _ipodLibraryRoot = root;
        _ipodLibrary = null;
        _ipodTracks.Clear();
        if (root is null)
        {
            if (_source == LibrarySource.Ipod) RenderLibrary();
            return;
        }
        try
        {
            var library = await Task.Run(() => Sink.Services.Ipod.IpodReader.Read(root));
            if (_ipodLibraryRoot != root) return; // device changed while loading
            _ipodLibrary = library;
            foreach (var t in library.Tracks) _ipodTracks.Add(AdaptIpodTrack(t));
            var readableName = library.DeviceName ?? _ipodDevice?.Name ?? "iPod";
            PlaybackStatus.Text = $"Read {library.Tracks.Count} track{(library.Tracks.Count == 1 ? "" : "s")} from {readableName}";
            ApplyIpodLibraryChrome(library, readableName);
            SyncPodcastStatusFromIpod();
        }
        catch (Exception ex)
        {
            Services.Log.Error("Reading iPod database failed", ex);
            PlaybackStatus.Text = $"Couldn't read the iPod database: {ex.Message}";
        }
        if (_source == LibrarySource.Ipod) RenderLibrary();
    }

    private void ApplyIpodLibraryChrome(Sink.Services.Ipod.IpodLibrary library, string name)
    {
        if (!_ipodConnected) return;
        var used = library.Tracks.Sum(t => t.SizeBytes);
        var lines = new List<string> { name };
        if (!string.IsNullOrWhiteSpace(library.ModelNumber)) lines.Add($"Model {library.ModelNumber}");
        if (library.CapacityBytes > 0)
            lines.Add($"{Bytes(library.CapacityBytes)} · {Bytes(library.CapacityBytes - used)} free");
        else if (used > 0)
            lines.Add($"{Bytes(used)} of music");
        var pl = library.Playlists.Count(p => !p.IsMaster);
        lines.Add($"{library.Tracks.Count} tracks · {pl} playlist{(pl == 1 ? "" : "s")}");
        if (!string.IsNullOrWhiteSpace(library.SerialNumber)) lines.Add($"Serial {library.SerialNumber}");
        IpodButton.ToolTip = string.Join("\n", lines);
        IpodMenuHeader.Header = library.CapacityBytes > 0 ? $"{name} · {Bytes(library.CapacityBytes)}" : name;
    }

    // Decimal units — this is how iTunes and Apple state iPod capacity.
    private static string Bytes(long value)
    {
        if (value <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = value;
        var u = 0;
        while (v >= 1000 && u < units.Length - 1) { v /= 1000; u++; }
        return $"{v:0.#} {units[u]}";
    }

    private static Track AdaptIpodTrack(Sink.Services.Ipod.IpodDbTrack t) => new()
    {
        Title = t.Title,
        Artist = string.IsNullOrWhiteSpace(t.Artist) ? "Unknown Artist" : t.Artist,
        Album = string.IsNullOrWhiteSpace(t.Album) ? "Unknown Album" : t.Album,
        Genre = string.IsNullOrWhiteSpace(t.Genre) ? "Unknown" : t.Genre,
        FileName = System.IO.Path.GetFileName(t.FilePath),
        FilePath = t.FilePath,
        TrackNumber = t.TrackNumber,
        Year = t.Year,
        Duration = t.Duration,
    };

    private void Category_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !Enum.TryParse(button.Name.Replace("Button", ""), out LibraryCategory category)) return;
        _source = LibrarySource.Music;
        _category = category; _drilldown = null; _activePlaylist = null; PlaylistList.SelectedItem = null;
        ApplySourceChrome();
        SetActiveNavigation(button);
        RenderLibrary();
    }

    private void IpodCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !Enum.TryParse(button.Name.Replace("Ipod", "").Replace("Button", ""), out LibraryCategory category)) return;
        _source = LibrarySource.Ipod;
        _category = category; _drilldown = null; _activePlaylist = null; PlaylistList.SelectedItem = null;
        ApplySourceChrome();
        SetActiveNavigation(button);
        RenderLibrary();
    }

    private void ShowMusicSource_Click(object sender, RoutedEventArgs e) => SetSource(LibrarySource.Music);
    private void ShowIpodSource_Click(object sender, RoutedEventArgs e) => SetSource(LibrarySource.Ipod);

    private void SetSource(LibrarySource source)
    {
        _source = source;
        _category = LibraryCategory.Albums;
        _drilldown = null; _activePlaylist = null; PlaylistList.SelectedItem = null;
        ApplySourceChrome();
        SetActiveNavigation(source == LibrarySource.Music ? AlbumsButton : IpodAlbumsButton);
        RenderLibrary();
    }

    private void ApplySourceChrome()
    {
        ExitDownloadView();
        ExitPodcastView();
        var music = _source == LibrarySource.Music;
        MusicNav.Visibility = music ? Visibility.Visible : Visibility.Collapsed;
        IpodNav.Visibility = music ? Visibility.Collapsed : Visibility.Visible;
        MusicHeaderButton.Tag = music ? "Active" : null;
        IpodHeaderButton.Tag = music ? null : "Active";
    }

    private void SetActiveNavigation(Button? active)
    {
        foreach (var button in new[] { ArtistsButton, AlbumsButton, GenresButton, SongsButton, IpodArtistsButton, IpodAlbumsButton, IpodGenresButton, IpodSongsButton })
        {
            var name = button.Name.Replace("Ipod", "").Replace("Button", "");
            button.Tag = button == active ? "Active" : name;
        }
    }

    private void RenderLibrary()
    {
        MetadataIndex.Rebuild(_tracks);
        var query = SearchBox?.Text?.Trim() ?? "";
        var ipodOnDevice = _source == LibrarySource.Ipod && _ipodLibrary is not null;
        var source = _source != LibrarySource.Ipod ? _tracks.ToList()
            : ipodOnDevice ? _ipodTracks.ToList()
            : _tracks.Where(track => _syncedTrackIds.Contains(track.Id)).ToList();
        IEnumerable<Track> visible = source;
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
            ViewTitle.Text = _drilldown ?? _activePlaylist?.Name ?? (_source == LibrarySource.Ipod ? "iPod · Songs" : "Songs");
            ViewSubtitle.Text = IpodSubtitle(rows.Count) ?? $"{rows.Count} tracks";
            return;
        }

        var groups = _category switch
        {
            LibraryCategory.Artists => source.GroupBy(track => track.Artist).Select(group => Card(group.Key, $"{group.Count()} tracks", group.Key, group)),
            LibraryCategory.Genres => source.GroupBy(track => track.Genre).Select(group => Card(group.Key, $"{group.Count()} tracks", group.Key, group)),
            _ => source.GroupBy(track => track.Album).Select(group => Card(group.Key, group.First().Artist, group.Key, group))
        };
        var cards = groups.Where(card => query.Length == 0 || card.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).OrderBy(card => card.Name).ToList();
        GroupsView.ItemsSource = cards;
        ViewTitle.Text = _source == LibrarySource.Ipod ? $"iPod · {_category}" : _category.ToString();
        ViewSubtitle.Text = IpodSubtitle(cards.Count) ?? $"{cards.Count} {_category.ToString().ToLowerInvariant()}";
    }

    private string? IpodSubtitle(int shown)
    {
        if (_source != LibrarySource.Ipod) return null;
        if (_ipodLibrary is not null)
        {
            var who = _ipodLibrary.DeviceName ?? _ipodDevice?.Name ?? "iPod";
            var playlists = _ipodLibrary.Playlists.Count(p => !p.IsMaster);
            return $"{_ipodLibrary.Tracks.Count} tracks · {playlists} playlist{(playlists == 1 ? "" : "s")} on {who}";
        }
        if (_ipodDevice is { CanReadDatabase: false })
            return "No iTunes database on this iPod yet — sync music to create one";
        return _syncedTrackIds.Count == 0 ? "Nothing synced to iPod yet — drag music onto IPOD" : null;
    }

    private static GroupCard Card(string name, string detail, string colorSeed, IEnumerable<Track> members)
    {
        var palette = new[] { "#273A78", "#6E354B", "#285D56", "#6C4D31" };
        var initial = string.IsNullOrEmpty(name) ? "?" : name[..1].ToUpperInvariant();
        var artPath = members.Select(t => t.ArtworkPath).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
        return new GroupCard(name, detail, initial,
            new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette[Math.Abs(colorSeed.GetHashCode()) % palette.Length])),
            LoadArtwork(artPath));
    }

    private static readonly Dictionary<string, ImageSource> _artCache = [];

    // Guards against a slow remote-art fetch landing after the user has moved on
    // to something else.
    private object? _nowPlayingArtToken;

    /// <summary>
    /// Points the bottom transport bar's cover at a local artwork path or a
    /// remote image URL (podcast show art), falling back to the letter tile.
    /// </summary>
    private void SetNowPlayingArt(string? source)
    {
        var token = new object();
        _nowPlayingArtToken = token;

        if (string.IsNullOrWhiteSpace(source))
        {
            PlayerArtImage.Source = null;
            PlayerArtImage.Visibility = Visibility.Collapsed;
            return;
        }

        if (!source.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            ApplyNowPlayingArt(LoadArtwork(source));
            return;
        }

        ApplyNowPlayingArt(null);
        _ = LoadRemoteNowPlayingArtAsync(source, token);
    }

    private async Task LoadRemoteNowPlayingArtAsync(string url, object token)
    {
        string? path = null;
        try { path = await Task.Run(() => Artwork.CacheRemote(url)); }
        catch (Exception ex) { Services.Log.Error("Now-playing art fetch failed", ex); }
        if (!ReferenceEquals(_nowPlayingArtToken, token)) return;
        ApplyNowPlayingArt(path is null ? null : LoadArtwork(path));
    }

    private void ApplyNowPlayingArt(ImageSource? art)
    {
        PlayerArtImage.Source = art;
        PlayerArtImage.Visibility = art is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static ImageSource? LoadArtwork(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (_artCache.TryGetValue(path, out var cached)) return cached;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 320;
            bitmap.EndInit();
            bitmap.Freeze();
            _artCache[path] = bitmap;
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private void GroupCard_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GroupsView.SelectedItem is not GroupCard card) return;
        _drilldown = card.Name;
        RenderLibrary();
        e.Handled = true;
    }

    private List<GroupCard> SelectedCards() => GroupsView.SelectedItems.OfType<GroupCard>().ToList();

    private List<Track> TracksForCards(IReadOnlyList<GroupCard> cards)
    {
        IEnumerable<Track> pool = _source == LibrarySource.Ipod
            ? _tracks.Where(t => _syncedTrackIds.Contains(t.Id))
            : _tracks;
        var names = cards.Select(c => c.Name).ToHashSet();
        return (_category switch
        {
            LibraryCategory.Artists => pool.Where(t => names.Contains(t.Artist)),
            LibraryCategory.Genres => pool.Where(t => names.Contains(t.Genre)),
            _ => pool.Where(t => names.Contains(t.Album))
        }).ToList();
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
        StopPreview("started a library track");
        if (_playingEpisode is not null) StopPodcast(markPlayed: false);
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
        PlayerArtInitial.Text = string.IsNullOrEmpty(track.Album) ? "♫" : track.Album[..1].ToUpperInvariant();
        SetNowPlayingArt(track.ArtworkPath);
        PlaybackStatus.Text = $"▶  Playing {track.Title} — {track.Artist}";
        PlayPauseButton.Content = "Ⅱ";
        UpdatePlayerDuration();
        _playbackTimer.Start();
        UpdateRecordSpin();
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (_previewNode is not null) { UpdatePreviewProgress(); return; }
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
        if (_previewNode is not null) { TogglePreviewPause(); return; }
        if (_playingEpisode is not null) { TogglePodcastPause(); return; }
        if (_nowPlaying is null) return;
        _isPlaying = !_isPlaying;
        if (_isPlaying) _mediaPlayer.Play(); else _mediaPlayer.Pause();
        PlayPauseButton.Content = _isPlaying ? "Ⅱ" : "▶";
        UpdateRecordSpin();
    }

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (_previewNode is not null) { StopPreview("skipped"); return; }
        if (_playingEpisode is not null) { SkipEpisode(-1); return; }
        Skip(-1);
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_previewNode is not null) { StopPreview("skipped"); return; }
        if (_playingEpisode is not null) { SkipEpisode(1); return; }
        NextTrack();
    }

    private void NextTrack() => Skip(1);

    private void Skip(int direction)
    {
        if (_nowPlaying is null || _tracks.Count == 0) return;
        var index = _tracks.IndexOf(_nowPlaying);
        PlayTrack(_tracks[(index + direction + _tracks.Count) % _tracks.Count]);
    }

    private void ProgressSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingProgress) return;
        if (_previewNode is not null)
        {
            _previewPlayer.Position = TimeSpan.FromSeconds(e.NewValue);
            return;
        }
        if (_playingEpisode is not null)
        {
            _podcastPlayer.Position = TimeSpan.FromSeconds(e.NewValue);
            return;
        }
        if (_nowPlaying is null) return;
        _simulatedPosition = TimeSpan.FromSeconds(e.NewValue);
        if (!string.IsNullOrWhiteSpace(_nowPlaying.FilePath)) _mediaPlayer.Position = _simulatedPosition;
        UpdatePlayerProgress();
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _mediaPlayer.Volume = e.NewValue;
        _podcastPlayer.Volume = e.NewValue;
        _previewPlayer.Volume = e.NewValue;
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
            var tooltip = _ipodDevice?.Tooltip ?? "Simulated iPod\n160 GB · 84 GB free\nClick the record to disconnect";
            var header = _ipodDevice is null ? "Simulated iPod · 160 GB" : _ipodDevice.Summary;
            RecordLabel.Fill = new SolidColorBrush(Color.FromRgb(40, 91, 184));
            IpodStateText.Text = "IPOD";
            IpodStateText.Foreground = new SolidColorBrush(Color.FromRgb(139, 124, 255));
            IpodButton.ToolTip = tooltip;
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

    /// <summary>Shows / hides a small spinning wheel next to a sidebar section button.</summary>
    private static void SpinIndicator(FrameworkElement spinner, bool on)
    {
        if (spinner.RenderTransform is not RotateTransform rt) return;
        if (on)
        {
            spinner.Visibility = Visibility.Visible;
            rt.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            rt.BeginAnimation(RotateTransform.AngleProperty, null);
            spinner.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateRecordSpin()
    {
        if (_ipodSyncing) return;
        var shouldSpin = (_isPlaying && _nowPlaying is not null)
                         || (_playingEpisode is not null && !_podcastPaused);
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
        foreach (var item in IpodMenu.Items.OfType<MenuItem>())
        {
            if (item == IpodMenuHeader || item == IpodRescanItem) continue;
            item.IsEnabled = _ipodConnected;
        }
    }

    private void RescanIpod_Click(object sender, RoutedEventArgs e)
    {
        PlaybackStatus.Text = "Scanning for a connected iPod…";
        PollForIpod(manual: true);
    }

    private void SyncIpod_Click(object sender, RoutedEventArgs e)
    {
        var header = (sender as MenuItem)?.Header as string ?? "";
        if (header is "Sync all" or "Sync music")
            SyncTracksToDevice(_tracks.Where(t => !t.ExcludedFromShuffle).ToList());
        else
            StartIpodSync();
    }

    private bool _ipodWriting;

    /// <summary>Syncs tracks to the connected iPod — really writes the iTunesDB when the device is readable, otherwise stages them in the pending list.</summary>
    private async void SyncTracksToDevice(IReadOnlyList<Track> tracks)
    {
        if (_ipodWriting) { PlaybackStatus.Text = "iPod is busy…"; return; }
        var root = _ipodDevice?.LibraryRoot;
        if (root is null)
        {
            MarkSynced(tracks.Where(t => !t.ExcludedFromShuffle).Select(t => t.Id), announce: true);
            if (_ipodConnected) StartIpodSync();
            return;
        }

        var payload = tracks.ToList();
        _ipodWriting = true;
        StartIpodSync(indefinite: true);
        var progress = SyncProgress();
        try
        {
            var result = await Task.Run(() => Sink.Services.Ipod.IpodWriteService.Sync(root, payload, progress));
            PlaybackStatus.Text = result.Summary;
        }
        catch (Exception ex)
        {
            // An async-void handler must never let an exception reach the
            // dispatcher — that takes the whole app down.
            Services.Log.Error("SyncTracksToDevice threw", ex);
            PlaybackStatus.Text = $"iPod sync failed: {ex.Message}";
        }
        finally
        {
            _ipodWriting = false;
            StopIpodSync();
            LoadIpodLibrary(root);
        }
    }

    /// <summary>
    /// A progress reporter that always updates the status line on the UI thread,
    /// whatever thread <c>Report</c> is called from. <see cref="Progress{T}"/>
    /// alone isn't enough — it captures whatever context it's constructed on.
    /// </summary>
    private IProgress<(int done, int total, string message)> SyncProgress()
    {
        void Update((int done, int total, string message) p)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Update(p)); return; }
            PlaybackStatus.Text = $"{p.message} ({p.done + 1}/{p.total})";
        }
        return new Progress<(int done, int total, string message)>(Update);
    }

    private async void UnsyncTracks(IReadOnlyList<Track> tracks)
    {
        var root = _ipodDevice?.LibraryRoot;
        if (root is null)
        {
            var removed = 0;
            foreach (var track in tracks)
                if (_syncedTrackIds.Remove(track.Id)) removed++;
            if (removed == 0) return;
            SaveLibrary();
            RenderLibrary();
            PlaybackStatus.Text = $"Removed {removed} track{(removed == 1 ? "" : "s")} from iPod";
            return;
        }

        if (_ipodWriting) { PlaybackStatus.Text = "iPod is busy…"; return; }
        var paths = tracks.Select(t => t.FilePath).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!).ToList();
        _ipodWriting = true;
        StartIpodSync(indefinite: true);
        try
        {
            var result = await Task.Run(() => Sink.Services.Ipod.IpodWriteService.Remove(root, paths));
            PlaybackStatus.Text = result.Summary;
        }
        catch (Exception ex)
        {
            Services.Log.Error("UnsyncTracks threw", ex);
            PlaybackStatus.Text = $"iPod update failed: {ex.Message}";
        }
        finally
        {
            _ipodWriting = false;
            StopIpodSync();
            LoadIpodLibrary(root);
        }
    }

    private void MarkSynced(IEnumerable<Guid> ids, bool announce = false)
    {
        var added = 0;
        foreach (var id in ids)
            if (_syncedTrackIds.Add(id)) added++;
        if (added == 0)
        {
            if (announce) PlaybackStatus.Text = "iPod already up to date";
            return;
        }
        SaveLibrary();
        if (_source == LibrarySource.Ipod) RenderLibrary();
        if (announce) PlaybackStatus.Text = $"Queued {added} track{(added == 1 ? "" : "s")} for the iPod";
    }

    private void IpodNav_DragOver(object sender, DragEventArgs e)
    {
        var canSync = e.Data.GetDataPresent(TrackDragFormat);
        e.Effects = canSync ? DragDropEffects.Copy : DragDropEffects.None;
        if (canSync && sender is Button button) button.Tag = "Active";
        e.Handled = true;
    }

    private void IpodNav_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Button button) button.Tag = _source == LibrarySource.Ipod ? "Active" : null;
    }

    private void IpodNav_Drop(object sender, DragEventArgs e)
    {
        IpodNav_DragLeave(sender, e);
        if (e.Data.GetData(TrackDragFormat) is not Guid[] trackIds) return;
        e.Handled = true;
        SyncTracksToDevice(_tracks.Where(t => trackIds.Contains(t.Id) && !t.ExcludedFromShuffle).ToList());
    }

    private void StartIpodSync(bool indefinite = false)
    {
        if (!_ipodConnected || _ipodSyncing) return;
        _ipodSyncing = true;
        _recordSpinning = false;
        SpinIndicator(IpodSpinner, true);
        IpodStateText.Text = "SYNCING";
        var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever };
        RecordRotation.BeginAnimation(RotateTransform.AngleProperty, spin);
        if (indefinite) return; // caller ends it with StopIpodSync()
        var stopTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.2) };
        stopTimer.Tick += (_, _) =>
        {
            stopTimer.Stop();
            StopIpodSync();
        };
        stopTimer.Start();
    }

    private void StopIpodSync()
    {
        RecordRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        SpinIndicator(IpodSpinner, false);
        _ipodSyncing = false;
        if (_ipodConnected) IpodStateText.Text = "IPOD";
        UpdateRecordSpin();
    }

    private void EjectIpod_Click(object sender, RoutedEventArgs e) => SetIpodConnected(false);

    private void IpodButton_DragOver(object sender, DragEventArgs e)
    {
        var canSync = _ipodConnected && (e.Data.GetDataPresent(TrackDragFormat) || e.Data.GetDataPresent(PodcastDragFormat));
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
        if (e.Data.GetData(PodcastDragFormat) is string episodeIdText && Guid.TryParse(episodeIdText, out var episodeId))
        {
            e.Handled = true;
            if (!_ipodConnected) { PlaybackStatus.Text = "Connect an iPod before syncing"; return; }
            SyncEpisodeToIpod(episodeId);
            return;
        }
        if (e.Data.GetData(TrackDragFormat) is not Guid[] trackIds) return;
        e.Handled = true;
        if (!_ipodConnected)
        {
            PlaybackStatus.Text = "Connect an iPod before syncing";
            return;
        }
        var syncable = _tracks.Where(track => trackIds.Contains(track.Id) && !track.ExcludedFromShuffle).ToList();
        if (syncable.Count == 0) { PlaybackStatus.Text = "Nothing to sync"; return; }
        SyncTracksToDevice(syncable);
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

    private void GroupCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _trackDragStart = e.GetPosition(this);

    private void GroupCard_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _trackDragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(position.Y - _trackDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var cards = SelectedCards();
        if (cards.Count == 0) return;
        var names = cards.Select(c => c.Name).ToHashSet();
        var ids = (_category switch
        {
            LibraryCategory.Artists => _tracks.Where(t => names.Contains(t.Artist)),
            LibraryCategory.Genres => _tracks.Where(t => names.Contains(t.Genre)),
            _ => _tracks.Where(t => names.Contains(t.Album))
        }).Select(t => t.Id).ToArray();
        if (ids.Length == 0) return;
        var data = new DataObject();
        data.SetData(TrackDragFormat, ids);
        DragDrop.DoDragDrop(GroupsView, data, DragDropEffects.Copy);
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
        if (added > 0) SaveLibrary();
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
        SaveLibrary();
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
        if (imported.Count > 0) SaveLibrary();
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
        SaveLibrary();
    }

    private void PlaylistList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlaylistList.SelectedItem is not Playlist playlist) return;
        _source = LibrarySource.Music; ApplySourceChrome();
        _activePlaylist = playlist; _category = LibraryCategory.Playlist; _drilldown = null; SetActiveNavigation(null); RenderLibrary();
    }

    // ---- Contextual right-click menus ------------------------------------

    private void TracksGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var tracks = TracksGrid.SelectedItems.OfType<Track>().ToList();
        if (tracks.Count == 0 && TracksGrid.SelectedItem is Track single) tracks.Add(single);
        if (tracks.Count == 0) { e.Handled = true; return; }

        var menu = TracksGrid.ContextMenu!;
        menu.Items.Clear();
        var label = tracks.Count == 1 ? tracks[0].Title : $"{tracks.Count} tracks";
        menu.Items.Add(Header(label));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Play", () => PlayTracks(tracks)));
        menu.Items.Add(Item("Edit metadata…", () => EditMetadata(tracks)));
        menu.Items.Add(AddToPlaylistMenu(tracks));
        if (_source == LibrarySource.Ipod)
        {
            menu.Items.Add(Item("Unsync from iPod", () => UnsyncTracks(tracks)));
        }
        else
        {
            menu.Items.Add(Item("Sync to iPod", () => SyncTracksToIpod(tracks)));
            menu.Items.Add(ExcludeFromShuffleItem(tracks));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Delete from library", () => DeleteTracks(tracks)));
    }

    private void GroupsView_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = GroupsView.ContextMenu!;
        var kind = _category;
        var cards = SelectedCards();
        if (cards.Count == 0) { e.Handled = true; return; }
        var tracks = TracksForCards(cards);
        if (tracks.Count == 0) { e.Handled = true; return; }
        var single = cards.Count == 1;

        menu.Items.Clear();
        menu.Items.Add(Header(single ? cards[0].Name : $"{cards.Count} {kind.ToString().ToLowerInvariant()}"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Play", () => PlayTracks(tracks)));
        if (kind == LibraryCategory.Genres)
        {
            if (single) menu.Items.Add(Item("Rename…", () => RenameGenre(cards[0].Name)));
        }
        else
            menu.Items.Add(Item("Edit metadata…", () => EditMetadata(tracks)));
        if (kind == LibraryCategory.Albums && single)
            menu.Items.Add(Item("Crop album art", () => CropAlbumArt(cards[0].Name, tracks)));
        menu.Items.Add(AddToPlaylistMenu(tracks));
        if (_source == LibrarySource.Ipod)
            menu.Items.Add(Item("Unsync from iPod", () => UnsyncTracks(tracks)));
        else
        {
            menu.Items.Add(Item("Sync to iPod", () => SyncTracksToIpod(tracks)));
            menu.Items.Add(ExcludeFromShuffleItem(tracks));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Delete from library", () => DeleteTracks(tracks)));
    }

    private static MenuItem Header(string text) => new() { Header = text, IsEnabled = false, FontWeight = FontWeights.SemiBold };

    private static MenuItem Item(string text, Action action)
    {
        var item = new MenuItem { Header = text };
        item.Click += (_, _) => action();
        return item;
    }

    private MenuItem AddToPlaylistMenu(IReadOnlyList<Track> tracks)
    {
        var parent = new MenuItem { Header = "Add to playlist" };
        if (_playlists.Count == 0)
        {
            parent.Items.Add(new MenuItem { Header = "No playlists", IsEnabled = false });
            return parent;
        }
        foreach (var playlist in _playlists)
        {
            var target = playlist;
            parent.Items.Add(Item(playlist.Name, () => AddTracksToPlaylist(target, tracks)));
        }
        return parent;
    }

    private MenuItem ExcludeFromShuffleItem(IReadOnlyList<Track> tracks)
    {
        var allExcluded = tracks.All(t => t.ExcludedFromShuffle);
        var item = new MenuItem { Header = "Exclude from iPod shuffle", IsCheckable = true, IsChecked = allExcluded };
        item.Click += (_, _) =>
        {
            var exclude = !allExcluded;
            foreach (var track in tracks) track.ExcludedFromShuffle = exclude;
            SaveLibrary();
            PlaybackStatus.Text = exclude
                ? $"Excluded {tracks.Count} track{(tracks.Count == 1 ? "" : "s")} from shuffle"
                : $"Included {tracks.Count} track{(tracks.Count == 1 ? "" : "s")} in shuffle";
        };
        return item;
    }

    private void PlayTracks(IReadOnlyList<Track> tracks)
    {
        if (tracks.Count > 0) PlayTrack(tracks[0]);
    }

    private void AddTracksToPlaylist(Playlist playlist, IReadOnlyList<Track> tracks)
    {
        var added = 0;
        foreach (var track in tracks)
        {
            if (playlist.TrackIds.Contains(track.Id)) continue;
            playlist.TrackIds.Add(track.Id);
            added++;
        }
        PlaylistList.Items.Refresh();
        if (added > 0) SaveLibrary();
        PlaybackStatus.Text = added > 0 ? $"Added {added} track{(added == 1 ? "" : "s")} to {playlist.Name}" : $"Already in {playlist.Name}";
    }

    private void SyncTracksToIpod(IReadOnlyList<Track> tracks)
    {
        var syncable = tracks.Where(t => !t.ExcludedFromShuffle).ToList();
        if (syncable.Count == 0) { PlaybackStatus.Text = "Nothing to sync"; return; }
        SyncTracksToDevice(syncable);
    }

    private void EditMetadata(IReadOnlyList<Track> tracks)
    {
        var dialog = new MetadataWindow(tracks) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        SaveLibrary();
        RenderLibrary();
        PlaybackStatus.Text = $"Updated {tracks.Count} track{(tracks.Count == 1 ? "" : "s")}";
    }

    private void RenameGenre(string genre)
    {
        var dialog = new TextPromptWindow { Owner = this, Suggestions = Controls.SuggestionField.Genre };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Answer)) return;
        var count = 0;
        foreach (var track in _tracks.Where(t => t.Genre == genre))
        {
            track.Genre = dialog.Answer;
            count++;
        }
        SaveLibrary();
        RenderLibrary();
        PlaybackStatus.Text = $"Renamed genre on {count} track{(count == 1 ? "" : "s")}";
    }

    /// <summary>Forces the album's cover art through the standard 300x300 centre-crop.</summary>
    private void CropAlbumArt(string album, IReadOnlyList<Track> tracks)
    {
        var artist = tracks.Select(t => t.Artist).FirstOrDefault() ?? "";
        var key = $"{album}|{artist}";
        var existing = tracks.Select(t => t.ArtworkPath).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));

        string? cropped = null;
        if (existing is not null && Artwork.Recrop(existing)) cropped = existing;
        if (cropped is null)
        {
            var withFile = tracks.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.FilePath) && File.Exists(t.FilePath!));
            if (withFile is not null) cropped = Artwork.ExtractAndCrop(withFile.FilePath!, key);
        }

        if (cropped is null)
        {
            PlaybackStatus.Text = $"No cover art to crop for {album}";
            return;
        }

        foreach (var track in tracks) track.ArtworkPath = cropped;
        _artCache.Clear();
        SaveLibrary();
        RenderLibrary();
        PlaybackStatus.Text = $"Cropped album art for {album}";
    }

    private void DeleteTracks(IReadOnlyList<Track> tracks)
    {
        var ids = tracks.Select(t => t.Id).ToHashSet();
        foreach (var track in _tracks.Where(t => ids.Contains(t.Id)).ToList()) _tracks.Remove(track);
        foreach (var playlist in _playlists)
            foreach (var id in playlist.TrackIds.Where(ids.Contains).ToList())
                playlist.TrackIds.Remove(id);
        _syncedTrackIds.RemoveWhere(ids.Contains);
        if (_nowPlaying is not null && ids.Contains(_nowPlaying.Id))
        {
            _mediaPlayer.Stop(); _mediaPlayer.Close(); _nowPlaying = null; _isPlaying = false; _playbackTimer.Stop();
            PlayerTitle.Text = "Choose something to play"; PlayerArtist.Text = "Your library is ready"; PlayerArtInitial.Text = "♫"; PlayPauseButton.Content = "▶";
            UpdateRecordSpin();
        }
        PlaylistList.Items.Refresh();
        SaveLibrary();
        RenderLibrary();
        PlaybackStatus.Text = $"Deleted {tracks.Count} track{(tracks.Count == 1 ? "" : "s")}";
    }

    private sealed record GroupCard(string Name, string Detail, string Initial, Brush Color, ImageSource? Art = null);
}
