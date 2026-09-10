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
    private readonly DispatcherTimer _podcastTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private PodcastEpisode? _playingEpisode;
    private Podcast? _playingShow;
    private bool _podcastPaused;
    private Point _episodeDragStart;

    private const string PodcastDragFormat = "Sink.PodcastEpisode";

    private void InitPodcasts()
    {
        _podcasts.AddRange(PodcastStore.Load());
        _podcastPlayer.Volume = 0.7;
        _podcastPlayer.MediaOpened += (_, _) =>
        {
            if (_playingEpisode is { PositionSeconds: > 1 })
                _podcastPlayer.Position = TimeSpan.FromSeconds(_playingEpisode.PositionSeconds);
            if (_playingEpisode is not null && _podcastPlayer.NaturalDuration.HasTimeSpan)
            {
                if (_playingEpisode.Duration <= TimeSpan.Zero)
                    _playingEpisode.Duration = _podcastPlayer.NaturalDuration.TimeSpan;
                ProgressSlider.Maximum = Math.Max(1, _podcastPlayer.NaturalDuration.TimeSpan.TotalSeconds);
            }
        };
        _podcastPlayer.MediaEnded += (_, _) => StopPodcast(markPlayed: true);
        _podcastTimer.Tick += (_, _) => PodcastTimer_Tick();
        Loaded += (_, _) => UpdatePodcastSidebarDot();
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
        UpdatePodcastSidebarDot();
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
            Log.Error($"Podcast feed load failed: {result.FeedUrl}", ex);
            PodcastStatus.Text = $"Couldn't load that feed: {ex.Message}";
        }
    }

    private void PodcastCard_DoubleClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Podcast podcast) return;
        _currentShow = podcast;
        _podcastMode = PodcastMode.Show;
        EpisodeFilterBox.Text = "";
        // Opening the show marks its new episodes as seen (clears the show dot;
        // per-episode dots clear as they render this pass).
        foreach (var episode in podcast.Episodes) episode.IsNew = false;
        PodcastStore.Save(_podcasts);
        RenderPodcasts();
        UpdatePodcastSidebarDot();
    }

    // ---- Per-show download rule (task 66) --------------------------------

    private bool _syncingRuleCombos;

    private void RuleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingRuleCombos || _currentShow is null) return;
        _currentShow.RuleCount = (RuleCountCombo.SelectedItem as ComboBoxItem)?.Tag is string tag && int.TryParse(tag, out var n) ? n : 0;
        _currentShow.RuleMode = RuleModeCombo.SelectedIndex == 1 ? PodcastRuleMode.Oldest : PodcastRuleMode.Newest;
        PodcastStore.Save(_podcasts);
        PodcastSubtitle.Text = $"{_currentShow.UnplayedCount} unplayed · {_currentShow.Episodes.Count} episodes · rule: {_currentShow.RuleCount} {_currentShow.RuleMode.ToString().ToLowerInvariant()}";
        _ = RunAutoDownloadsAsync(_currentShow);
    }

    private void LoadRuleCombos(Podcast show)
    {
        _syncingRuleCombos = true;
        var idx = show.RuleCount switch { 0 => 0, 1 => 1, 2 => 2, 3 => 3, 5 => 4, _ => show.RuleCount >= 10 ? 5 : 3 };
        RuleCountCombo.SelectedIndex = idx;
        RuleModeCombo.SelectedIndex = show.RuleMode == PodcastRuleMode.Oldest ? 1 : 0;
        _syncingRuleCombos = false;
    }

    private void UpdatePodcastSidebarDot()
    {
        PodcastsNewDot.Visibility = _podcasts.Any(p => p.HasNewUnplayed) ? Visibility.Visible : Visibility.Collapsed;
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

    private int _podcastDownloads;

    private async Task DownloadEpisodeAsync(Podcast podcast, PodcastEpisode episode)
    {
        var dir = Path.Combine(PodcastFolder(), Sanitize(podcast.Title));
        PodcastStatus.Text = $"Downloading {episode.Title}…";
        if (_podcastDownloads++ == 0) SpinIndicator(PodcastSpinner, true);
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
            Log.Error($"Podcast episode download failed: {episode.Title}", ex);
            PodcastStatus.Text = $"Download failed: {ex.Message}";
        }
        finally
        {
            if (--_podcastDownloads <= 0) { _podcastDownloads = 0; SpinIndicator(PodcastSpinner, false); }
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

    // ---- Show-level bulk actions (task 76) ------------------------------

    private void MarkAllPlayed_Click(object sender, RoutedEventArgs e)
    {
        if (_currentShow is null) return;
        var changed = 0;
        foreach (var episode in _currentShow.Episodes)
        {
            if (episode.IsPlayed) continue;
            episode.IsPlayed = true;
            episode.PositionSeconds = episode.Duration.TotalSeconds;
            if (episode.IsDownloaded) PodcastRules.DropDownload(episode);
            changed++;
        }
        if (_playingEpisode is not null && _currentShow.Episodes.Contains(_playingEpisode)) StopPodcast(markPlayed: true);
        PodcastStore.Save(_podcasts);
        RenderPodcasts();
        UpdatePodcastSidebarDot();
        _ = RunAutoDownloadsAsync(_currentShow);
        PodcastStatus.Text = changed > 0 ? $"Marked {changed} episode{(changed == 1 ? "" : "s")} played" : "All episodes already played";
    }

    private async void DownloadAllEpisodes_Click(object sender, RoutedEventArgs e)
    {
        if (_currentShow is null) return;
        var pending = _currentShow.Episodes.Where(ep => !ep.IsPlayed && !ep.IsDownloaded).ToList();
        if (pending.Count == 0) { PodcastStatus.Text = "Nothing to download"; return; }
        var done = 0;
        foreach (var episode in pending)
        {
            PodcastStatus.Text = $"Downloading {done + 1}/{pending.Count} — {episode.Title}";
            await DownloadEpisodeAsync(_currentShow, episode);
            if (episode.IsDownloaded) done++;
            RenderPodcasts();
        }
        PodcastStatus.Text = $"Downloaded {done} of {pending.Count} episode{(pending.Count == 1 ? "" : "s")}";
    }

    private void DeleteAllDownloads_Click(object sender, RoutedEventArgs e)
    {
        if (_currentShow is null) return;
        var removed = 0;
        foreach (var episode in _currentShow.Episodes.Where(ep => ep.IsDownloaded).ToList())
        {
            if (_playingEpisode == episode) StopPodcast(markPlayed: false);
            PodcastRules.DropDownload(episode);
            removed++;
        }
        PodcastStore.Save(_podcasts);
        RenderPodcasts();
        PodcastStatus.Text = removed > 0 ? $"Deleted {removed} download{(removed == 1 ? "" : "s")}" : "No downloads to delete";
    }

    private void EpisodePlay_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PodcastEpisode episode) return;
        if (_playingEpisode == episode) { TogglePodcastPause(); return; }
        var show = _podcasts.FirstOrDefault(p => p.Episodes.Contains(episode)) ?? _currentShow;
        if (show is null) return;
        PlayEpisode(show, episode);
    }

    /// <summary>Starts an episode and hands the bottom transport bar over to it.</summary>
    private void PlayEpisode(Podcast show, PodcastEpisode episode)
    {
        var uri = episode.IsDownloaded ? new Uri(episode.LocalPath!) : SafeUri(episode.AudioUrl);
        if (uri is null) { PodcastStatus.Text = "That episode has no playable audio"; return; }

        StopPreview("started a podcast");

        // Take over from music playback.
        _mediaPlayer.Stop();
        _mediaPlayer.Close();
        _nowPlaying = null;
        _isPlaying = false;
        _playbackTimer.Stop();

        _playingShow = show;
        _playingEpisode = episode;
        _podcastPaused = false;
        _podcastPlayer.Open(uri);
        _podcastPlayer.Play();
        _podcastTimer.Start();

        PlayerTitle.Text = episode.Title;
        PlayerArtist.Text = show.Title;
        PlayerArtInitial.Text = string.IsNullOrEmpty(show.Title) ? "🎙" : show.Title[..1].ToUpperInvariant();
        SetNowPlayingArt(show.ArtworkUrl);
        PlayPauseButton.Content = "Ⅱ";
        ProgressSlider.Maximum = Math.Max(1, episode.Duration.TotalSeconds);
        _updatingProgress = true;
        ProgressSlider.Value = Math.Min(ProgressSlider.Maximum, episode.PositionSeconds);
        _updatingProgress = false;
        PlaybackStatus.Text = $"▶  {episode.Title}";
        PodcastStatus.Text = $"▶  {episode.Title} — {show.Title}";
        UpdateRecordSpin();
        RenderPodcasts();
    }

    private void TogglePodcastPause()
    {
        if (_playingEpisode is null) return;
        _podcastPaused = !_podcastPaused;
        if (_podcastPaused) _podcastPlayer.Pause(); else _podcastPlayer.Play();
        PlayPauseButton.Content = _podcastPaused ? "▶" : "Ⅱ";
        UpdateRecordSpin();
    }

    private void SkipEpisode(int direction)
    {
        if (_playingShow is null || _playingEpisode is null) return;
        var list = _playingShow.Episodes.OrderByDescending(x => x.Published).ToList();
        var idx = list.IndexOf(_playingEpisode) + direction;
        if (idx < 0 || idx >= list.Count) return;
        PlayEpisode(_playingShow, list[idx]);
    }

    private void PodcastTimer_Tick()
    {
        if (_playingEpisode is null) return;
        var pos = _podcastPlayer.Position;
        if (pos > TimeSpan.Zero) _playingEpisode.PositionSeconds = pos.TotalSeconds;
        if (PodcastRules.ShouldMarkPlayed(_playingEpisode) && !_playingEpisode.IsPlayed)
            _playingEpisode.IsPlayed = true;

        // Drive the bottom transport bar.
        var total = TimeSpan.FromSeconds(ProgressSlider.Maximum);
        _updatingProgress = true;
        ProgressSlider.Value = Math.Min(ProgressSlider.Maximum, pos.TotalSeconds);
        _updatingProgress = false;
        ElapsedText.Text = FormatTime(pos);
        RemainingText.Text = $"-{FormatTime(total - pos)}";
    }

    private void StopPodcast(bool markPlayed)
    {
        _podcastTimer.Stop();
        if (_playingEpisode is not null)
        {
            if (markPlayed) _playingEpisode.IsPlayed = true;
            if (_playingEpisode.IsPlayed && _playingEpisode.IsDownloaded) PodcastRules.DropDownload(_playingEpisode);
            PodcastStore.Save(_podcasts);
            var show = _playingShow ?? _podcasts.FirstOrDefault(p => p.Episodes.Contains(_playingEpisode));
            if (show is not null) _ = RunAutoDownloadsAsync(show);
        }
        _podcastPlayer.Stop();
        _podcastPlayer.Close();
        _playingEpisode = null;
        _playingShow = null;
        _podcastPaused = false;

        // Reset the bottom transport bar.
        PlayPauseButton.Content = "▶";
        PlayerTitle.Text = "Choose something to play";
        PlayerArtist.Text = "Your library is ready";
        PlayerArtInitial.Text = "♫";
        SetNowPlayingArt(null);
        _updatingProgress = true;
        ProgressSlider.Value = 0;
        _updatingProgress = false;
        ElapsedText.Text = "0:00";
        RemainingText.Text = "-0:00";
        UpdateRecordSpin();
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
    private async void SyncEpisodeToIpod(Guid episodeId)
    {
        try
        {
            var show = _podcasts.FirstOrDefault(p => p.Episodes.Any(x => x.Id == episodeId));
            var episode = show?.Episodes.FirstOrDefault(x => x.Id == episodeId);
            if (show is null || episode is null) return;

            if (!episode.IsDownloaded)
            {
                PodcastStatus.Text = "Downloading before syncing…";
                await DownloadEpisodeAsync(show, episode);
            }
            if (episode.IsDownloaded) PushEpisodeTrack(show, episode);
            else PodcastStatus.Text = "Couldn't download that episode to sync";
        }
        catch (Exception ex)
        {
            Log.Error("SyncEpisodeToIpod threw", ex);
            PodcastStatus.Text = $"Couldn't sync that episode: {ex.Message}";
        }
    }

    private void PushEpisodeTrack(Podcast show, PodcastEpisode episode)
    {
        if (!_ipodConnected) { PodcastStatus.Text = "Connect an iPod before syncing"; return; }
        if (_ipodWriting) { PodcastStatus.Text = "iPod is busy…"; return; }
        if (string.IsNullOrEmpty(episode.LocalPath) || !System.IO.File.Exists(episode.LocalPath))
        {
            PodcastStatus.Text = "Episode file is missing";
            return;
        }
        var track = new Track
        {
            Title = episode.Title,
            Artist = show.Title,
            Album = show.Title,
            Genre = "Podcast",
            FileName = Path.GetFileName(episode.LocalPath),
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
        UpdatePodcastSidebarDot();
        foreach (var podcast in _podcasts.ToList()) await RunAutoDownloadsAsync(podcast);
    }

    // ---- iPod play-status + bookmark sync (tasks 63, 67) ----------------

    /// <summary>
    /// Reconciles podcast state with a connected iPod: pulls the device's resume
    /// bookmark back into the matching episode, marks an episode played when the
    /// device shows a play count or a near-complete bookmark, then pushes our
    /// newer positions out to the device. Falls back to play-count-only when the
    /// DB library can't surface the bookmark field.
    /// </summary>
    private void SyncPodcastStatusFromIpod()
    {
        var root = _ipodDevice?.LibraryRoot;
        if (_ipodLibrary is null || root is null || _podcasts.Count == 0) return;

        var byName = _ipodLibrary.Tracks
            .Where(t => !string.IsNullOrWhiteSpace(t.FilePath))
            .GroupBy(t => Path.GetFileName(t.FilePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var changed = false;
        var toPush = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var episode in _podcasts.SelectMany(p => p.Episodes))
        {
            if (string.IsNullOrEmpty(episode.LocalPath)) continue;
            var name = Path.GetFileName(episode.LocalPath);
            if (!byName.TryGetValue(name, out var deviceTrack)) continue;

            // Pull: take the further-along position between library and device.
            if (deviceTrack.BookmarkMs > episode.PositionSeconds * 1000 + 1500)
            {
                episode.PositionSeconds = deviceTrack.BookmarkMs / 1000.0;
                episode.IpodBookmarkMs = deviceTrack.BookmarkMs;
                changed = true;
            }

            var nearEnd = episode.Duration > TimeSpan.Zero && episode.PositionSeconds >= episode.Duration.TotalSeconds * 0.95;
            if (!episode.IsPlayed && (deviceTrack.PlayCount > 0 || nearEnd))
            {
                episode.IsPlayed = true;
                episode.PositionSeconds = episode.Duration.TotalSeconds;
                if (episode.IsDownloaded) PodcastRules.DropDownload(episode);
                changed = true;
            }

            // Push: our position is ahead of the device's bookmark.
            var ourMs = (long)(episode.PositionSeconds * 1000);
            if (!episode.IsPlayed && ourMs > deviceTrack.BookmarkMs + 1500)
                toPush[name] = ourMs;
        }

        if (toPush.Count > 0 && !_ipodWriting)
        {
            _ = Task.Run(() => Sink.Services.Ipod.IpodWriteService.WritePodcastPositions(root, toPush))
                .ContinueWith(t =>
                {
                    if (t.Status == TaskStatus.RanToCompletion && t.Result > 0)
                        Dispatcher.Invoke(() => PlaybackStatus.Text = $"Synced {t.Result} podcast position{(t.Result == 1 ? "" : "s")} to iPod");
                });
        }

        if (!changed) return;
        PodcastStore.Save(_podcasts);
        if (_podcastViewActive) { RenderPodcasts(); UpdatePodcastSidebarDot(); }
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
                LoadRuleCombos(_currentShow);
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
