using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Sink.Models;
using Sink.Services;

namespace Sink;

public enum PodcastMode { Library, Search, Show }

/// <summary>
/// Podcasts: subscribe from an iTunes directory search, browse shows and
/// episodes, download / play / mark-played with per-show auto-download rules,
/// and reconcile play status with a connected iPod.
/// </summary>
public partial class MainWindow
{
    private readonly List<Podcast> _podcasts = [];
    private IReadOnlyList<PodcastSearchResult> _searchResults = [];
    private Podcast? _currentShow;
    private PodcastMode _podcastMode = PodcastMode.Library;
    private bool _podcastViewActive;

    private readonly MediaPlayer _podcastPlayer = new();
    private readonly DispatcherTimer _podcastTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private PodcastEpisode? _playingEpisode;
    private Point _episodeDragStart;

    private const string PodcastDragFormat = "Sink.PodcastEpisode";

    private void InitPodcasts()
    {
        _podcasts.AddRange(PodcastStore.Load());
        _podcastPlayer.MediaOpened += (_, _) =>
        {
            if (_playingEpisode is { PositionSeconds: > 1 })
                _podcastPlayer.Position = TimeSpan.FromSeconds(_playingEpisode.PositionSeconds);
            if (_playingEpisode is not null && _podcastPlayer.NaturalDuration.HasTimeSpan && _playingEpisode.Duration <= TimeSpan.Zero)
                _playingEpisode.Duration = _podcastPlayer.NaturalDuration.TimeSpan;
        };
        _podcastPlayer.MediaEnded += (_, _) => StopPodcast(markPlayed: true);
        _podcastTimer.Tick += (_, _) => PodcastTimer_Tick();
    }

    // ---- View / mode switching ---------------------------------------------

    private void ShowPodcasts_Click(object sender, RoutedEventArgs e)
    {
        _podcastViewActive = true;
        MusicPage.Visibility = Visibility.Collapsed;
        DownloadPage.Visibility = Visibility.Collapsed;
        PodcastPage.Visibility = Visibility.Visible;
        IpodCanvas.Visibility = Visibility.Visible;

        MusicNav.Visibility = Visibility.Collapsed;
        IpodNav.Visibility = Visibility.Collapsed;
        PodcastNav.Visibility = Visibility.Visible;
        MusicHeaderButton.Tag = null;
        IpodHeaderButton.Tag = null;
        DownloadHeaderButton.Tag = null;
        PodcastsHeaderButton.Tag = "Active";
        SetActiveNavigation(null);

        _currentShow = null;
        _podcastMode = PodcastMode.Library;
        SetPodcastNav();
        RenderPodcasts();
        _ = RefreshAllFeedsAsync();
    }

    private void ExitPodcastView()
    {
        if (!_podcastViewActive) return;
        _podcastViewActive = false;
        PodcastPage.Visibility = Visibility.Collapsed;
        MusicPage.Visibility = Visibility.Visible;
        PodcastNav.Visibility = Visibility.Collapsed;
        PodcastsHeaderButton.Tag = null;
    }

    private void PodcastMode_Click(object sender, RoutedEventArgs e)
    {
        _podcastMode = (sender as Button)?.Name == "PodcastSearchButton" ? PodcastMode.Search : PodcastMode.Library;
        _currentShow = null;
        SetPodcastNav();
        RenderPodcasts();
    }

    private void PodcastBack_Click(object sender, RoutedEventArgs e)
    {
        _currentShow = null;
        _podcastMode = PodcastMode.Library;
        SetPodcastNav();
        RenderPodcasts();
    }

    private void SetPodcastNav()
    {
        PodcastLibraryButton.Tag = _podcastMode == PodcastMode.Library ? "Active" : "Library";
        PodcastSearchButton.Tag = _podcastMode == PodcastMode.Search ? "Active" : "Search";
    }

    // ---- Search + subscribe ----------------------------------------------

    private async void PodcastSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var term = PodcastSearchBox.Text.Trim();
        if (term.Length == 0) return;
        _podcastMode = PodcastMode.Search;
        _currentShow = null;
        SetPodcastNav();
        PodcastStatus.Text = $"Searching for “{term}”…";
        try
        {
            _searchResults = await PodcastService.SearchAsync(term);
            PodcastStatus.Text = $"{_searchResults.Count} result{(_searchResults.Count == 1 ? "" : "s")}";
        }
        catch (Exception ex)
        {
            _searchResults = [];
            PodcastStatus.Text = $"Search failed: {ex.Message}";
        }
        RenderPodcasts();
    }

    private async void Subscribe_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PodcastSearchResult result) return;
        if (_podcasts.Any(p => string.Equals(p.FeedUrl, result.FeedUrl, StringComparison.OrdinalIgnoreCase)))
        {
            PodcastStatus.Text = "Already subscribed";
            return;
        }
        PodcastStatus.Text = $"Loading {result.Title}…";
        try
        {
            var podcast = await PodcastService.LoadFeedAsync(result.FeedUrl);
            _podcasts.Add(podcast);
            PodcastStore.Save(_podcasts);
            PodcastStatus.Text = $"Subscribed to {podcast.Title}";
            _podcastMode = PodcastMode.Library;
            SetPodcastNav();
            RenderPodcasts();
            await RunAutoDownloadsAsync(podcast);
        }
        catch (Exception ex)
        {
            PodcastStatus.Text = $"Couldn't load that feed: {ex.Message}";
        }
    }

    private void PodcastCard_DoubleClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Podcast podcast) return;
        _currentShow = podcast;
        _podcastMode = PodcastMode.Show;
        EpisodeFilterBox.Text = "";
        RenderPodcasts();
    }

    // ---- Episode actions ------------------------------------------------

    private async void EpisodeDownload_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PodcastEpisode episode || _currentShow is null) return;
        if (episode.IsDownloaded)
        {
            PodcastRules.DropDownload(episode);
            PodcastStore.Save(_podcasts);
            PodcastStatus.Text = "Deleted download";
            RenderPodcasts();
            return;
        }
        await DownloadEpisodeAsync(_currentShow, episode);
        RenderPodcasts();
    }

    private async Task DownloadEpisodeAsync(Podcast podcast, PodcastEpisode episode)
    {
        var dir = Path.Combine(PodcastFolder(), Sanitize(podcast.Title));
        PodcastStatus.Text = $"Downloading {episode.Title}…";
        try
        {
            var progress = new Progress<double>(p => PodcastStatus.Text = $"Downloading {episode.Title} — {p * 100:0}%");
            var path = await PodcastService.DownloadEpisodeAsync(episode, dir, progress);
            episode.LocalPath = path;
            episode.DownloadedAt = DateTime.UtcNow;
            PodcastStore.Save(_podcasts);
            PodcastStatus.Text = $"Downloaded {episode.Title}";
        }
        catch (Exception ex)
        {
            PodcastStatus.Text = $"Download failed: {ex.Message}";
        }
    }

    private void EpisodeMarkPlayed_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PodcastEpisode episode) return;
        episode.IsPlayed = !episode.IsPlayed;
        if (episode.IsPlayed)
        {
            episode.PositionSeconds = episode.Duration.TotalSeconds;
            if (episode.IsDownloaded) PodcastRules.DropDownload(episode);
        }
        else
        {
            episode.PositionSeconds = 0;
        }
        PodcastStore.Save(_podcasts);
        RenderPodcasts();
        if (_currentShow is not null) _ = RunAutoDownloadsAsync(_currentShow);
    }

    private void EpisodePlay_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PodcastEpisode episode) return;
        if (_playingEpisode == episode)
        {
            StopPodcast(markPlayed: false);
            return;
        }
        var uri = episode.IsDownloaded ? new Uri(episode.LocalPath!) : SafeUri(episode.AudioUrl);
        if (uri is null) { PodcastStatus.Text = "That episode has no playable audio"; return; }

        _mediaPlayer.Pause(); // pause any music
        _playingEpisode = episode;
        _podcastPlayer.Open(uri);
        _podcastPlayer.Play();
        _podcastTimer.Start();
        PodcastStatus.Text = $"▶  {episode.Title}";
    }

    private void PodcastTimer_Tick()
    {
        if (_playingEpisode is null) return;
        if (_podcastPlayer.Position > TimeSpan.Zero) _playingEpisode.PositionSeconds = _podcastPlayer.Position.TotalSeconds;
        if (PodcastRules.ShouldMarkPlayed(_playingEpisode) && !_playingEpisode.IsPlayed)
        {
            _playingEpisode.IsPlayed = true;
        }
    }

    private void StopPodcast(bool markPlayed)
    {
        _podcastTimer.Stop();
        if (_playingEpisode is not null)
        {
            if (markPlayed) _playingEpisode.IsPlayed = true;
            if (_playingEpisode.IsPlayed && _playingEpisode.IsDownloaded) PodcastRules.DropDownload(_playingEpisode);
            PodcastStore.Save(_podcasts);
            var show = _podcasts.FirstOrDefault(p => p.Episodes.Contains(_playingEpisode));
            if (show is not null) _ = RunAutoDownloadsAsync(show);
        }
        _podcastPlayer.Stop();
        _podcastPlayer.Close();
        _playingEpisode = null;
        RenderPodcasts();
    }

    // ---- Filter / sort ------------------------------------------------

    private void EpisodeFilter_Changed(object sender, RoutedEventArgs e) { if (_currentShow is not null) RenderEpisodes(); }
    private void EpisodeSort_Changed(object sender, SelectionChangedEventArgs e) { if (_currentShow is not null) RenderEpisodes(); }

    // ---- Drag an episode onto the iPod ------------------------------------

    private void Episode_MouseDown(object sender, MouseButtonEventArgs e) => _episodeDragStart = e.GetPosition(this);

    private void Episode_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if ((sender as FrameworkElement)?.DataContext is not PodcastEpisode episode) return;
        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _episodeDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _episodeDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var data = new DataObject();
        data.SetData(PodcastDragFormat, episode.Id.ToString());
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
    }

    /// <summary>Called from IpodButton_Drop when the payload is a podcast episode.</summary>
    private void SyncEpisodeToIpod(Guid episodeId)
    {
        var episode = _podcasts.SelectMany(p => p.Episodes).FirstOrDefault(x => x.Id == episodeId);
        if (episode is null) return;
        if (!episode.IsDownloaded)
        {
            var show = _podcasts.First(p => p.Episodes.Contains(episode));
            PodcastStatus.Text = "Downloading before syncing…";
            _ = DownloadEpisodeAsync(show, episode).ContinueWith(_ =>
                Dispatcher.Invoke(() => { if (episode.IsDownloaded) PushEpisodeTrack(episode); }));
            return;
        }
        PushEpisodeTrack(episode);
    }

    private void PushEpisodeTrack(PodcastEpisode episode)
    {
        var show = _podcasts.First(p => p.Episodes.Contains(episode));
        var track = new Track
        {
            Title = episode.Title,
            Artist = show.Title,
            Album = show.Title,
            Genre = "Podcast",
            FileName = Path.GetFileName(episode.LocalPath!),
            FilePath = episode.LocalPath,
            Duration = episode.Duration,
        };
        SyncTracksToDevice([track]);
        PodcastStatus.Text = $"Syncing {episode.Title} to iPod";
    }

    // ---- Auto-download rules (task 62) ----------------------------------

    private async Task RunAutoDownloadsAsync(Podcast podcast)
    {
        PodcastRules.Reconcile(podcast);
        foreach (var episode in PodcastRules.DesiredDownloads(podcast))
        {
            if (episode.IsDownloaded) continue;
            await DownloadEpisodeAsync(podcast, episode);
        }
        PodcastStore.Save(_podcasts);
        RenderPodcasts();
    }

    private async Task RefreshAllFeedsAsync()
    {
        foreach (var podcast in _podcasts.ToList())
        {
            try
            {
                var fresh = await PodcastService.LoadFeedAsync(podcast.FeedUrl);
                PodcastService.MergeFeed(podcast, fresh);
            }
            catch (Exception) { /* offline / bad feed — keep what we have */ }
            PodcastRules.Reconcile(podcast);
        }
        PodcastStore.Save(_podcasts);
        RenderPodcasts();
        foreach (var podcast in _podcasts.ToList()) await RunAutoDownloadsAsync(podcast);
    }

    // ---- iPod play-status sync (task 63) --------------------------------

    /// <summary>
    /// Pulls play status back from a connected iPod: an episode whose synced file
    /// shows a play count on the device is marked played in the library, which
    /// lets the auto-downloader advance. (The iTunesDB exposes play count but not
    /// a resumable bookmark, so partial positions are not round-tripped.)
    /// </summary>
    private void SyncPodcastStatusFromIpod()
    {
        if (_ipodLibrary is null || _podcasts.Count == 0) return;
        var playedOnDevice = _ipodLibrary.Tracks
            .Where(t => t.PlayCount > 0 && !string.IsNullOrWhiteSpace(t.FilePath))
            .Select(t => Path.GetFileName(t.FilePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (playedOnDevice.Count == 0) return;

        var changed = false;
        foreach (var episode in _podcasts.SelectMany(p => p.Episodes))
        {
            if (episode.IsPlayed || string.IsNullOrEmpty(episode.LocalPath)) continue;
            if (!playedOnDevice.Contains(Path.GetFileName(episode.LocalPath))) continue;
            episode.IsPlayed = true;
            episode.PositionSeconds = episode.Duration.TotalSeconds;
            if (episode.IsDownloaded) PodcastRules.DropDownload(episode);
            changed = true;
        }
        if (!changed) return;
        PodcastStore.Save(_podcasts);
        if (_podcastViewActive) RenderPodcasts();
        foreach (var podcast in _podcasts.ToList()) _ = RunAutoDownloadsAsync(podcast);
    }

    // ---- Rendering ----------------------------------------------------

    private void RenderPodcasts()
    {
        if (!_podcastViewActive) return;
        var show = _currentShow is not null;
        _podcastMode = show ? PodcastMode.Show : _podcastMode;

        PodcastLibraryView.Visibility = _podcastMode == PodcastMode.Library ? Visibility.Visible : Visibility.Collapsed;
        PodcastSearchView.Visibility = _podcastMode == PodcastMode.Search ? Visibility.Visible : Visibility.Collapsed;
        EpisodeView.Visibility = _podcastMode == PodcastMode.Show ? Visibility.Visible : Visibility.Collapsed;
        PodcastShowToolbar.Visibility = _podcastMode == PodcastMode.Show ? Visibility.Visible : Visibility.Collapsed;
        PodcastBackButton.Visibility = _podcastMode == PodcastMode.Show ? Visibility.Visible : Visibility.Collapsed;

        switch (_podcastMode)
        {
            case PodcastMode.Library:
                PodcastLibraryView.ItemsSource = null;
                PodcastLibraryView.ItemsSource = _podcasts;
                PodcastTitle.Text = "Library";
                PodcastSubtitle.Text = _podcasts.Count == 0 ? "" : $"{_podcasts.Count} show{(_podcasts.Count == 1 ? "" : "s")} subscribed";
                PodcastEmptyHint.Visibility = _podcasts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                PodcastEmptyHint.Text = "No podcasts yet — use Search to find a show and Subscribe.";
                break;
            case PodcastMode.Search:
                PodcastSearchView.ItemsSource = _searchResults;
                PodcastTitle.Text = "Search";
                PodcastSubtitle.Text = "Type a show name and press Enter";
                PodcastEmptyHint.Visibility = _searchResults.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                PodcastEmptyHint.Text = "Type a podcast name above and press Enter.";
                break;
            case PodcastMode.Show:
                PodcastTitle.Text = _currentShow!.Title;
                PodcastSubtitle.Text = $"{_currentShow.UnplayedCount} unplayed · {_currentShow.Episodes.Count} episodes · rule: {_currentShow.RuleCount} {_currentShow.RuleMode.ToString().ToLowerInvariant()}";
                RenderEpisodes();
                break;
        }
    }

    private void RenderEpisodes()
    {
        if (_currentShow is null) return;
        IEnumerable<PodcastEpisode> episodes = _currentShow.Episodes;

        var filter = EpisodeFilterBox.Text.Trim();
        if (filter.Length > 0)
            episodes = episodes.Where(e => e.Title.Contains(filter, StringComparison.OrdinalIgnoreCase));
        if (EpisodeUnplayedOnly.IsChecked == true)
            episodes = episodes.Where(e => !e.IsPlayed);

        episodes = EpisodeSort.SelectedIndex switch
        {
            1 => episodes.OrderBy(e => e.Published),
            2 => episodes.OrderBy(e => e.Title),
            3 => episodes.OrderByDescending(e => e.EpisodeNumber),
            _ => episodes.OrderByDescending(e => e.Published),
        };

        var list = episodes.ToList();
        EpisodeView.ItemsSource = null;
        EpisodeView.ItemsSource = list;
        PodcastEmptyHint.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PodcastEmptyHint.Text = "No episodes match.";
    }

    // ---- helpers ----------------------------------------------------

    private static string PodcastFolder()
    {
        var configured = AppSettings.Current.PodcastLocation;
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(LibraryStore.Directory, "Podcasts")
            : configured;
    }

    private static Uri? SafeUri(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u : null;

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }
}
