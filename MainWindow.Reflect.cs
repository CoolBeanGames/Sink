using System.Windows;
using System.Windows.Input;
using Sink.Models;
using Sink.Services;

namespace Sink;

/// <summary>
/// The Reflect page (task 127): year-to-date listening stats. Listens are
/// logged as <see cref="ListenEvent"/>s when playback of a song or podcast
/// episode stops — naturally finishing or being switched away from — as
/// long as at least 30 seconds (or the whole thing, if shorter) was
/// actually heard, so skipping through a library doesn't inflate counts.
/// "This year" is the calendar year (Jan 1 – now).
/// </summary>
public partial class MainWindow
{
    private readonly List<ListenEvent> _listenEvents = [];
    private bool _reflectViewActive;

    private void InitReflect() => _listenEvents.AddRange(ReflectStore.Load());

    /// <summary>Flushes whatever's mid-playback as a listen when the app closes, rather than losing it silently.</summary>
    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        RecordSongListenIfDue();
        if (_playingEpisode is not null) StopPodcast(markPlayed: false);
    }

    // ---- Recording ---------------------------------------------------

    /// <summary>Logs a listen for whatever song is currently playing, if enough of it was heard. Call right before switching _nowPlaying away from it.</summary>
    private void RecordSongListenIfDue()
    {
        if (_nowPlaying is null) return;
        var full = _nowPlaying.Duration;
        var threshold = TimeSpan.FromSeconds(Math.Min(30, Math.Max(1, full.TotalSeconds)));
        if (_simulatedPosition < threshold) return;
        var completed = full > TimeSpan.Zero && _simulatedPosition >= full - TimeSpan.FromSeconds(2);
        _listenEvents.Add(new ListenEvent { Kind = ListenKind.Song, ItemId = _nowPlaying.Id, Duration = _simulatedPosition, Completed = completed });
        ReflectStore.Save(_listenEvents);
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
    /// identity IpodDbTrack.Key already uses for its own matching.
    /// </summary>
    private void SyncMusicPlayCountsFromIpod(Services.Ipod.IpodLibrary library)
    {
        var deviceId = library.SerialNumber;
        if (string.IsNullOrWhiteSpace(deviceId)) return; // no stable per-device identity to baseline against
        var byKey = library.Tracks.Where(t => !t.IsPodcast).ToLookup(t => t.Key);
        var recorded = 0;
        foreach (var track in _tracks)
        {
            var key = Services.Ipod.IpodDbTrack.MakeKey(track.Title, track.Artist, track.Album, track.TrackNumber);
            var deviceTrack = byKey[key].FirstOrDefault();
            if (deviceTrack is null) continue;

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
            track.SyncedIpodPlayCounts[deviceId] = deviceCount;
        }
        if (recorded == 0) return;
        ReflectStore.Save(_listenEvents);
        SaveLibrary();
        Services.Log.Info($"Reflect: folded in {recorded} iPod play{(recorded == 1 ? "" : "s")} from {deviceId}");
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

        MusicNav.Visibility = Visibility.Collapsed;
        IpodNav.Visibility = Visibility.Collapsed;
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
