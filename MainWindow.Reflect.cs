using System.Windows;
using System.Windows.Input;
using Sink.Models;
using Sink.Services;

namespace Sink;

/// <summary>
/// The Reflect page (task 127): year-to-date listening stats. A song listen
/// is logged as soon as at least 30 seconds (or the whole thing, if
/// shorter) has actually been heard — live during playback, not only once
/// it stops — so skipping through a library doesn't inflate counts but a
/// song played once and left alone still gets counted. A podcast listen is
/// still logged when playback of the episode stops. "This year" is the
/// calendar year (Jan 1 – now).
/// </summary>
public partial class MainWindow
{
    private readonly List<ListenEvent> _listenEvents = [];
    private bool _reflectViewActive;

    private void InitReflect()
    {
        _listenEvents.AddRange(ReflectStore.Load());
        RecomputePlayCounts();
    }

    /// <summary>
    /// Refreshes every track's PlayCount from _listenEvents — cheap enough to
    /// call right before any render, so the Songs list's PLAYS column is
    /// always current without needing its own targeted refresh at each of
    /// the several places listen events change (in-app plays, iPod sync).
    /// </summary>
    private void RecomputePlayCounts()
    {
        var counts = _listenEvents.Where(e => e.Kind == ListenKind.Song)
            .GroupBy(e => e.ItemId).ToDictionary(g => g.Key, g => g.Count());
        foreach (var track in _tracks) track.PlayCount = counts.GetValueOrDefault(track.Id, 0);
    }

    /// <summary>Flushes whatever's mid-playback as a listen when the app closes, rather than losing it silently.</summary>
    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        RecordSongListenIfDue();
        if (_playingEpisode is not null) StopPodcast(markPlayed: false);
    }

    // ---- Recording ---------------------------------------------------

    /// <summary>True once the currently-playing song has already been recorded this play, so PlaybackTimer_Tick's live check and a later switch-away/close don't double-count it. Reset in PlayTrack.</summary>
    private bool _nowPlayingListenRecorded;

    /// <summary>
    /// Records a listen for the current song the moment enough of it has
    /// been heard (matching this class's own documented threshold), instead
    /// of waiting for playback to actually stop — a song played once and
    /// never switched away from (or the app never closed) previously never
    /// got recorded at all, which looked exactly like "plays aren't tracked"
    /// even though it would have counted eventually (task: "Song Play Counts").
    /// Called every tick while playing; a no-op once already recorded or
    /// before the threshold.
    /// </summary>
    private void RecordSongListenIfDue()
    {
        if (_nowPlaying is null || _nowPlayingListenRecorded) return;
        var full = _nowPlaying.Duration;
        var threshold = TimeSpan.FromSeconds(Math.Min(30, Math.Max(1, full.TotalSeconds)));
        if (_simulatedPosition < threshold) return;
        var completed = full > TimeSpan.Zero && _simulatedPosition >= full - TimeSpan.FromSeconds(2);
        _listenEvents.Add(new ListenEvent { Kind = ListenKind.Song, ItemId = _nowPlaying.Id, Duration = _simulatedPosition, Completed = completed });
        ReflectStore.Save(_listenEvents);
        _nowPlayingListenRecorded = true;
        // Live-refresh the PLAYS column (and Tags page) right away rather
        // than waiting for some unrelated re-render to happen to pick it up.
        RenderLibrary();
        _tagsView?.Refresh();
    }

    /// <summary>Logs a podcast listen. Called from StopPodcast with the elapsed position captured before it resets.</summary>
    private void RecordPodcastListen(Guid episodeId, TimeSpan elapsed, bool completed)
    {
        if (elapsed < TimeSpan.FromSeconds(30) && !completed) return;
        _listenEvents.Add(new ListenEvent { Kind = ListenKind.PodcastEpisode, ItemId = episodeId, Duration = elapsed, Completed = completed });
        ReflectStore.Save(_listenEvents);
    }

    /// <summary>
    /// Reconciles song play counts against a just-read iPod library, diffing
    /// each track's on-device PlayCount against the baseline recorded at the
    /// last sync so the same device plays never get folded into Reflect twice
    /// (task 128). Matched by (Title, Artist, Album, TrackNumber) — the same
    /// identity IpodDbTrack.Key already uses for its own matching. Returns how
    /// many new plays were folded in, for the "Sync changes" completion message.
    /// </summary>
    private int SyncMusicPlayCountsFromIpod(Services.Ipod.IpodLibrary library)
    {
        var deviceId = library.SerialNumber;
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            // This used to be a bare, silent `return` — every prior report of
            // "listen counts still not syncing" left no trace of why, because
            // nothing here ever logged anything at all. If this is still the
            // failure mode, the log will now say so explicitly instead of the
            // guesswork this has taken so far.
            Services.Log.Warn("Reflect: iPod has no usable SerialNumber (checked SysInfo + SysInfoExtended) — can't baseline play counts against it");
            return 0;
        }
        var deviceMusic = library.Tracks.Where(t => !t.IsPodcast).ToList();
        var byKey = deviceMusic.ToLookup(t => t.Key);
        var recorded = 0;
        var matched = 0;
        var deltaZero = 0;
        foreach (var track in _tracks)
        {
            var key = Services.Ipod.IpodDbTrack.MakeKey(track.Title, track.Artist, track.Album, track.TrackNumber);
            var deviceTrack = byKey[key].FirstOrDefault();
            if (deviceTrack is null) continue;
            matched++;

            var deviceCount = Math.Max(0, deviceTrack.PlayCount);
            track.SyncedIpodPlayCounts ??= [];
            var baseline = track.SyncedIpodPlayCounts.GetValueOrDefault(deviceId, 0);
            var delta = deviceCount - baseline;
            if (delta > 0)
            {
                for (var i = 0; i < delta; i++)
                    _listenEvents.Add(new ListenEvent { Kind = ListenKind.Song, ItemId = track.Id, Duration = track.Duration, Completed = true });
                recorded += delta;
            }
            else deltaZero++;
            track.SyncedIpodPlayCounts[deviceId] = deviceCount;
        }
        // Always log a summary line (not just on a successful fold-in) so a
        // sync that finds nothing to record still shows *why*: no keys
        // matched at all (library vs device metadata disagrees — sample keys
        // from both sides below to compare directly) versus keys matched but
        // every device PlayCount was already at its recorded baseline (no
        // new plays since the last sync, or the baseline over-counted).
        Services.Log.Info($"Reflect music sync: device {deviceId}, {_tracks.Count} local track(s), {deviceMusic.Count} device music track(s), {matched} matched by key, {deltaZero} matched-but-no-new-plays, {recorded} new play(s) recorded");
        if (matched == 0 && _tracks.Count > 0 && deviceMusic.Count > 0)
        {
            var localSample = _tracks.Take(3).Select(t => $"\"{Services.Ipod.IpodDbTrack.MakeKey(t.Title, t.Artist, t.Album, t.TrackNumber)}\"");
            var deviceSample = deviceMusic.Take(3).Select(t => $"\"{t.Key}\"");
            Services.Log.Warn($"Reflect music sync: zero key matches — local sample [{string.Join(", ", localSample)}] vs device sample [{string.Join(", ", deviceSample)}]");
        }
        if (recorded == 0) return 0;
        ReflectStore.Save(_listenEvents);
        SaveLibrary();
        Services.Log.Info($"Reflect: folded in {recorded} iPod play{(recorded == 1 ? "" : "s")} from {deviceId}");
        return recorded;
    }

    // ---- View switching -------------------------------------------------

    private void ShowReflectSource_Click(object sender, RoutedEventArgs e)
    {
        ExitDownloadView();
        ExitPodcastView();
        ExitTagsView();
        _reflectViewActive = true;
        MusicPage.Visibility = Visibility.Collapsed;
        DownloadPage.Visibility = Visibility.Collapsed;
        PodcastPage.Visibility = Visibility.Collapsed;
        TagsPage.Visibility = Visibility.Collapsed;
        IpodCanvas.Visibility = Visibility.Collapsed;
        ReflectPage.Visibility = Visibility.Visible;

        SetNavExpanded(MusicNav, MusicNavTransform, false);
        SetNavExpanded(IpodNav, IpodNavTransform, false);
        MusicHeaderButton.Tag = null;
        IpodHeaderButton.Tag = null;
        DownloadHeaderButton.Tag = null;
        TagsHeaderButton.Tag = null;
        ReflectHeaderButton.Tag = "Active";
        SetActiveNavigation(null);

        RenderReflect();
    }

    private void ExitReflectView()
    {
        if (!_reflectViewActive) return;
        _reflectViewActive = false;
        ReflectPage.Visibility = Visibility.Collapsed;
        MusicPage.Visibility = Visibility.Visible;
        IpodCanvas.Visibility = Visibility.Visible;
        ReflectHeaderButton.Tag = null;
    }

    // ---- Stats -----------------------------------------------------------

    private sealed record RankedRow(string Name, string Detail);

    private void RenderReflect()
    {
        ReflectYearSubtitle.Text = $"{DateTime.Now.Year} so far";
        var yearStart = new DateTime(DateTime.Now.Year, 1, 1);
        var thisYear = _listenEvents.Where(ev => ev.Occurred >= yearStart).ToList();
        var songEvents = thisYear.Where(ev => ev.Kind == ListenKind.Song).ToList();
        var podcastEvents = thisYear.Where(ev => ev.Kind == ListenKind.PodcastEpisode).ToList();
        var tracksById = _tracks.ToDictionary(t => t.Id);

        var totalTime = TimeSpan.FromTicks(thisYear.Sum(ev => ev.Duration.Ticks));
        ReflectTotalTimeText.Text = FormatDuration(totalTime);

        var podcastTime = TimeSpan.FromTicks(podcastEvents.Sum(ev => ev.Duration.Ticks));
        ReflectPodcastHoursText.Text = FormatDuration(podcastTime);

        ReflectEpisodesCompletedText.Text = podcastEvents.Count(ev => ev.Completed).ToString();

        ReflectTopSongs.ItemsSource = songEvents.GroupBy(ev => ev.ItemId)
            .Select(g => (Track: tracksById.GetValueOrDefault(g.Key), Count: g.Count()))
            .Where(x => x.Track is not null)
            .OrderByDescending(x => x.Count)
            .Take(10)
            .Select(x => new RankedRow(x.Track!.Title, $"{x.Track.Artist} — {ListenText(x.Count)}"))
            .ToList();

        ReflectTopAlbums.ItemsSource = songEvents
            .Select(ev => tracksById.GetValueOrDefault(ev.ItemId))
            .Where(t => t is not null && !string.IsNullOrWhiteSpace(t.Album))
            .GroupBy(t => (t!.Album, t.Artist))
            .Select(g => (g.Key.Album, g.Key.Artist, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .Take(10)
            .Select(x => new RankedRow(x.Album, $"{x.Artist} — {ListenText(x.Count)}"))
            .ToList();

        ReflectTopArtists.ItemsSource = songEvents
            .Select(ev => tracksById.GetValueOrDefault(ev.ItemId))
            .Where(t => t is not null && !string.IsNullOrWhiteSpace(t.Artist))
            .GroupBy(t => t!.Artist)
            .Select(g => (Artist: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .Take(10)
            .Select(x => new RankedRow(x.Artist, ListenText(x.Count)))
            .ToList();

        ReflectTopGenres.ItemsSource = songEvents
            .Select(ev => tracksById.GetValueOrDefault(ev.ItemId))
            .Where(t => t is not null && !string.IsNullOrWhiteSpace(t.Genre))
            .GroupBy(t => t!.Genre)
            .Select(g => (Genre: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .Take(5)
            .Select(x => new RankedRow(x.Genre, ListenText(x.Count)))
            .ToList();

        var showByEpisode = _podcasts.SelectMany(p => p.Episodes.Select(ep => (ep.Id, Show: p))).ToDictionary(x => x.Id, x => x.Show);
        ReflectTopPodcasts.ItemsSource = podcastEvents
            .Select(ev => showByEpisode.GetValueOrDefault(ev.ItemId))
            .Where(show => show is not null)
            .GroupBy(show => show!.Title)
            .Select(g => (Title: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .Take(5)
            .Select(x => new RankedRow(x.Title, ListenText(x.Count)))
            .ToList();
    }

    private static string ListenText(int count) => $"{count} listen{(count == 1 ? "" : "s")}";

    private static string FormatDuration(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{span.TotalHours:0.#} hrs" : $"{(int)span.TotalMinutes} min";

    // ---- Favorites (heart icon, task 127) ---------------------------------

    private void ToggleFavorite(IReadOnlyList<Track> tracks)
    {
        if (tracks.Count == 0) return;
        var makeFavorite = tracks.Any(t => !t.IsFavorite);
        foreach (var track in tracks) track.IsFavorite = makeFavorite;
        SaveLibrary();
        RenderLibrary();
        _tagsView?.Refresh();
        PlaybackStatus.Text = makeFavorite
            ? $"Favorited {tracks.Count} track{(tracks.Count == 1 ? "" : "s")}"
            : $"Removed {tracks.Count} track{(tracks.Count == 1 ? "" : "s")} from favorites";
    }

    /// <summary>The heart glyph column on the Music page's track grid.</summary>
    private void FavoriteGlyph_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Track track) return;
        e.Handled = true;
        ToggleFavorite([track]);
        TracksGrid.Items.Refresh();
    }
}
