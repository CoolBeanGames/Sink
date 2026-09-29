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
        if (BackfillListenSnapshots()) ReflectStore.Save(_listenEvents);
        RecomputePlayCounts();
    }

    /// <summary>
    /// Copies song/show details onto listen events recorded before events
    /// carried their own, while the item is still in the library — so a later
    /// delete doesn't erase it from Reflect. Returns true if anything changed.
    /// </summary>
    private bool BackfillListenSnapshots()
    {
        var tracksById = _tracks.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        var changed = false;
        foreach (var ev in _listenEvents)
        {
            if (ev.HasSnapshot) continue;
            if (ev.Kind == ListenKind.Song && tracksById.TryGetValue(ev.ItemId, out var track))
            {
                ev.SnapshotFrom(track);
                changed = true;
            }
            else if (ev.Kind == ListenKind.PodcastEpisode && SnapshotPodcast(ev))
                changed = true;
        }
        return changed;
    }

    private bool SnapshotPodcast(ListenEvent ev)
    {
        foreach (var show in _podcasts)
            foreach (var episode in show.Episodes)
                if (episode.Id == ev.ItemId)
                {
                    ev.Title = episode.Title;
                    ev.Show = show.Title;
                    return true;
                }
        return false;
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
        
        Sink.Services.AppSettings.Current.SidebarWidth = SidebarColumn.Width.Value;
        Sink.Services.AppSettings.Current.OptFormatIndex = OptFormat.SelectedIndex;
        Sink.Services.AppSettings.Current.OptQualityIndex = OptQuality.SelectedIndex;
        Sink.Services.AppSettings.Current.OptMetadata = OptMetadata.IsChecked ?? true;
        Sink.Services.AppSettings.Current.OptAlbumArt = OptAlbumArt.IsChecked ?? true;
        Sink.Services.AppSettings.Current.OptMusicMeta = OptMusicMeta.IsChecked ?? true;
        Sink.Services.AppSettings.Current.OptNumberTracks = OptNumberTracks.IsChecked ?? false;
        Sink.Services.AppSettings.Current.OptCreatePlaylist = OptCreatePlaylist.IsChecked ?? false;
        Sink.Services.AppSettings.Current.Save();
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
        var listen = new ListenEvent { Kind = ListenKind.Song, ItemId = _nowPlaying.Id, Duration = _simulatedPosition, Completed = completed };
        listen.SnapshotFrom(_nowPlaying);
        _listenEvents.Add(listen);
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
        var listen = new ListenEvent { Kind = ListenKind.PodcastEpisode, ItemId = episodeId, Duration = elapsed, Completed = completed };
        SnapshotPodcast(listen);
        _listenEvents.Add(listen);
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
                    {
                        var listen = new ListenEvent { Kind = ListenKind.Song, ItemId = track.Id, Duration = track.Duration, Completed = true };
                        listen.SnapshotFrom(track);
                        _listenEvents.Add(listen);
                    }
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
        if (BackfillListenSnapshots()) ReflectStore.Save(_listenEvents);
        var podcastEvents = thisYear.Where(ev => ev.Kind == ListenKind.PodcastEpisode).ToList();
        var tracksById = _tracks.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        // A song still in the library shows its current details; a deleted
        // one falls back to what was recorded with the listen itself.
        var songEvents = thisYear.Where(ev => ev.Kind == ListenKind.Song)
            .Select(ev => (Event: ev, Info: tracksById.TryGetValue(ev.ItemId, out var t)
                ? new ListenInfo(t.Title, t.Artist, t.Album, t.Genre)
                : ev.HasSnapshot ? new ListenInfo(ev.Title!, ev.Artist ?? "", ev.Album ?? "", ev.Genre ?? "") : null))
            .Where(x => x.Info is not null)
            .Select(x => (x.Event, Info: x.Info!))
            .ToList();

        var totalTime = TimeSpan.FromTicks(thisYear.Sum(ev => ev.Duration.Ticks));
        ReflectTotalTimeText.Text = FormatDuration(totalTime);

        var podcastTime = TimeSpan.FromTicks(podcastEvents.Sum(ev => ev.Duration.Ticks));
        ReflectPodcastHoursText.Text = FormatDuration(podcastTime);

        ReflectEpisodesCompletedText.Text = podcastEvents.Count(ev => ev.Completed).ToString();

        ReflectTopSongs.ItemsSource = songEvents.GroupBy(x => (x.Info.Title.ToLowerInvariant(), x.Info.Artist.ToLowerInvariant()))
            .Select(g => (g.First().Info, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .Take(10)
            .Select(x => new RankedRow(x.Info.Title, $"{x.Info.Artist} — {ListenText(x.Count)}"))
            .ToList();

        ReflectTopAlbums.ItemsSource = songEvents
            .Select(x => x.Info)
            .Where(i => !string.IsNullOrWhiteSpace(i.Album))
            .GroupBy(i => (i.Album, i.Artist))
            .Select(g => (g.Key.Album, g.Key.Artist, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .Take(10)
            .Select(x => new RankedRow(x.Album, $"{x.Artist} — {ListenText(x.Count)}"))
            .ToList();

        ReflectTopArtists.ItemsSource = songEvents
            .Select(x => x.Info)
            .Where(i => !string.IsNullOrWhiteSpace(i.Artist))
            .GroupBy(i => i.Artist)
            .Select(g => (Artist: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .Take(10)
            .Select(x => new RankedRow(x.Artist, ListenText(x.Count)))
            .ToList();

        ReflectTopGenres.ItemsSource = songEvents
            .Select(x => x.Info)
            .Where(i => !string.IsNullOrWhiteSpace(i.Genre))
            .GroupBy(i => i.Genre)
            .Select(g => (Genre: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .Take(5)
            .Select(x => new RankedRow(x.Genre, ListenText(x.Count)))
            .ToList();

        var showByEpisode = _podcasts.SelectMany(p => p.Episodes.Select(ep => (ep.Id, Show: p)))
            .GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().Show.Title);
        ReflectTopPodcasts.ItemsSource = podcastEvents
            .Select(ev => showByEpisode.GetValueOrDefault(ev.ItemId) ?? ev.Show)
            .Where(show => !string.IsNullOrWhiteSpace(show))
            .GroupBy(show => show!)
            .Select(g => (Title: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .Take(5)
            .Select(x => new RankedRow(x.Title, ListenText(x.Count)))
            .ToList();
    }

    private sealed record ListenInfo(string Title, string Artist, string Album, string Genre);

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
