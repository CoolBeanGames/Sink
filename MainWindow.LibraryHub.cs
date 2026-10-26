using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Sink.Models;
using Sink.Services;

namespace Sink;

public partial class MainWindow
{
    private readonly LibraryPlaybackHistory _libraryPlaybackHistory = LibraryPlaybackHistory.Load();
    private TimeSpan _pendingLibrarySeek;
    private DateTime _lastLibraryPositionSavedUtc;
    private bool _libraryPlaybackFinished;

    private sealed record HubItem(string Name, string Detail, string Initial, Brush Color,
        string? ArtPath, Track Track, string? Key = null, double ResumeSeconds = 0)
    {
        public ImageSource? Art => LoadArtwork(ArtPath);
    }

    private static HubItem HubCard(string name, string detail, Track track, string? key = null,
        string? artPath = null, double resumeSeconds = 0)
    {
        var group = Card(name, detail, name, [track]);
        return new HubItem(name, detail, group.Initial, group.Color, artPath ?? group.ArtPath, track, key, resumeSeconds);
    }

    private static bool HubPlayable(Track track) => !string.IsNullOrWhiteSpace(track.FilePath) && File.Exists(track.FilePath);
    private static bool HubMayBePlayable(Track track) => !string.IsNullOrWhiteSpace(track.FilePath) && !track.IsMissing;

    private void RenderLibraryHub()
    {
        var newest = _tracks.Reverse().ToList();
        var tracks = newest.Take(8).Select(t => HubCard(t.Title, t.Artist, t)).ToList();
        // GroupBy keeps the first occurrence in library insertion order, so an
        // older artist or album doesn't become "new" when another song is added.
        var albums = _tracks.GroupBy(t => (t.Album.ToUpperInvariant(), t.Artist.ToUpperInvariant())).Reverse()
            .Take(8).Select(g => HubCard(g.First().Album, g.First().Artist, g.Last(), g.First().Album)).ToList();
        var artists = _tracks.GroupBy(t => t.Artist, StringComparer.OrdinalIgnoreCase).Reverse()
            .Take(8).Select(g => HubCard(g.Key, $"{g.Count()} tracks", g.Last(), g.Key,
                Artwork.CachedPath($"artist:{g.Key}"))).ToList();

        var byId = _tracks.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        var historyIds = _libraryPlaybackHistory.Entries.Select(e => e.TrackId).ToHashSet();
        var recent = _libraryPlaybackHistory.Entries
            .Where(e => byId.ContainsKey(e.TrackId))
            .Select(e => (Id: e.TrackId, When: e.LastStartedUtc, Position: e.Completed ? 0 : e.PositionSeconds))
            .Concat(_listenEvents.Where(e => e.Kind == ListenKind.Song && byId.ContainsKey(e.ItemId) && !historyIds.Contains(e.ItemId))
                .Select(e => (Id: e.ItemId, When: e.Occurred.ToUniversalTime(), Position: 0d)))
            .OrderByDescending(e => e.When).DistinctBy(e => e.Id)
            .Where(e => HubMayBePlayable(byId[e.Id])).Take(8)
            .Select(e =>
            {
                var track = byId[e.Id];
                var position = _nowPlaying?.Id == track.Id ? _simulatedPosition.TotalSeconds : e.Position;
                if (track.Duration > TimeSpan.Zero && position >= track.Duration.TotalSeconds - 2) position = 0;
                return HubCard(track.Title, position > 1 ? $"Resume · {FormatTime(TimeSpan.FromSeconds(position))} · {track.Artist}" : $"Replay · {track.Artist}", track, resumeSeconds: position);
            }).ToList();

        HubNewTracks.ItemsSource = tracks; HubNewTracksEmpty.Visibility = tracks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HubNewAlbums.ItemsSource = albums; HubNewAlbumsEmpty.Visibility = albums.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HubNewArtists.ItemsSource = artists; HubNewArtistsEmpty.Visibility = artists.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HubRecent.ItemsSource = recent; HubRecentEmpty.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var unplayed = _tracks.Count(t => t.PlayCount == 0 && HubMayBePlayable(t));
        HubRandomButton.IsEnabled = unplayed > 0;
        HubRandomDescription.Text = unplayed > 0
            ? $"Pick a random track you haven't played yet · {unplayed} available"
            : "No unplayed tracks with available files yet.";
    }

    private void HubTrack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: HubItem item } || !CheckHubTrack(item.Track)) return;
        PlayTrack(item.Track);
        RenderLibraryHub();
    }

    private void HubRecent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: HubItem item } || !CheckHubTrack(item.Track)) return;
        if (_nowPlaying?.Id == item.Track.Id && !_libraryPlaybackFinished)
        {
            if (!_isPlaying) PlayPause_Click(sender, e);
        }
        else PlayTrack(item.Track, item.ResumeSeconds > 1 ? TimeSpan.FromSeconds(item.ResumeSeconds) : null);
        RenderLibraryHub();
    }

    private void HubRandom_Click(object sender, RoutedEventArgs e)
    {
        RecomputePlayCounts();
        var choices = _tracks.Where(t => t.PlayCount == 0 && HubMayBePlayable(t)).ToList();
        while (choices.Count > 0)
        {
            var index = Random.Shared.Next(choices.Count);
            var track = choices[index];
            if (HubPlayable(track)) { PlayTrack(track); RenderLibraryHub(); return; }
            track.IsMissing = true;
            choices.RemoveAt(index);
        }
        PlaybackStatus.Text = "No unplayed tracks with available files yet.";
        RenderLibraryHub();
    }

    private bool CheckHubTrack(Track track)
    {
        if (HubPlayable(track)) return true;
        track.IsMissing = true;
        PlaybackStatus.Text = $"Can't find the audio file for {track.Title}.";
        RenderLibraryHub();
        return false;
    }

    private void HubArtist_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: HubItem item }) return;
        _libraryHubActive = false; _category = LibraryCategory.Albums; _lastMusicCategory = LibraryCategory.Artists;
        _artistFilter = item.Key; _drilldown = null;
        SetActiveNavigation(ArtistsButton); RenderLibrary();
    }

    private void HubAlbum_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: HubItem item }) return;
        _libraryHubActive = false; _category = LibraryCategory.Albums; _lastMusicCategory = LibraryCategory.Albums;
        _artistFilter = item.Track.Artist; _drilldown = item.Key;
        SetActiveNavigation(AlbumsButton); RenderLibrary();
    }

    private void ApplyPendingLibrarySeek()
    {
        if (_pendingLibrarySeek <= TimeSpan.Zero || _nowPlaying is null) return;
        var position = _pendingLibrarySeek;
        _pendingLibrarySeek = TimeSpan.Zero;
        if (_mediaPlayer.NaturalDuration.HasTimeSpan && position >= _mediaPlayer.NaturalDuration.TimeSpan)
            position = TimeSpan.Zero;
        _mediaPlayer.Position = position;
        _simulatedPosition = position;
    }

    private void SaveLibraryPlayback(bool completed = false)
    {
        if (_nowPlaying is null || !_tracks.Contains(_nowPlaying) || _libraryPlaybackFinished) return;
        _libraryPlaybackHistory.Update(_nowPlaying.Id, _simulatedPosition, _nowPlaying.Duration, completed);
        _libraryPlaybackFinished = completed;
        _lastLibraryPositionSavedUtc = DateTime.UtcNow;
    }

    private void SaveLibraryPlaybackPeriodically()
    {
        if (DateTime.UtcNow - _lastLibraryPositionSavedUtc >= TimeSpan.FromSeconds(10)) SaveLibraryPlayback();
    }
}
