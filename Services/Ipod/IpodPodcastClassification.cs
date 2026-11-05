using System.Reflection;
using Clickwheel;
using CwTrack = Clickwheel.Parsers.iTunesDB.Track;
using CwPlaylist = Clickwheel.Parsers.iTunesDB.Playlist;
using CwMediaType = Clickwheel.Parsers.iTunesDB.MediaType;

namespace Sink.Services.Ipod;

/// <summary>
/// Podcasts belong in the global track database and the special Podcasts
/// playlist, but NOT in the master music playlist. See libgpod's README:
/// https://github.com/gtkpod/libgpod/blob/master/README (audio HOWTO).
/// Clickwheel adds every new track to the master playlist automatically.
/// </summary>
internal static class IpodPodcastClassification
{
    // Clickwheel's public RemoveTrack rejects master and smart playlists.
    // Its internal overload updates both playlist representations and dirty
    // state without deleting the track or its media, unlike Tracks.Remove.
    private static readonly MethodInfo RemovePlaylistEntry = typeof(CwPlaylist).GetMethod(
        "RemoveTrack", BindingFlags.Instance | BindingFlags.NonPublic,
        null, [typeof(CwTrack), typeof(bool)], null)
        ?? throw new NotSupportedException("This version of Clickwheel cannot repair podcast playlist membership.");

    public static bool IsPodcast(CwTrack track) => track.PodcastFlag
        || (track.MediaType & CwMediaType.Podcast) != 0
        || string.Equals(track.Genre, "Podcast", StringComparison.OrdinalIgnoreCase);

    public static bool Mark(CwTrack track)
    {
        var type = track.IsVideo || (track.MediaType & CwMediaType.Video) != 0
            ? CwMediaType.VideoPodcast : CwMediaType.Podcast;
        var changed = false;
        if (!track.PodcastFlag) { track.PodcastFlag = true; changed = true; }
        if (track.MediaType != type) { track.MediaType = type; changed = true; }
        if (!track.RememberPlaybackPosition) { track.RememberPlaybackPosition = true; changed = true; }
        if (IpodShuffleFlag.Set(track, true)) changed = true;
        if (changed) IpodShuffleFlag.MarkDirty(track);
        return changed;
    }

    /// <returns>Number of existing tracks whose flags or membership were repaired.</returns>
    public static int Normalize(IPod ipod, IReadOnlySet<string>? knownEpisodeKeys = null)
    {
        var playlists = new List<CwPlaylist>();
        foreach (var playlist in ipod.Playlists) playlists.Add(playlist);
        var podcastLists = playlists.Where(p => p.IsPodcastPlaylist || p.Name == "Podcasts").ToList();
        var memberIds = podcastLists.SelectMany(p => p.Tracks).Select(t => t.Id).ToHashSet();
        var episodes = new List<CwTrack>();
        foreach (var track in ipod.Tracks)
            if (IsPodcast(track) || memberIds.Contains(track.Id)
                || knownEpisodeKeys?.Contains(IpodDbTrack.MakeKey(track.Title, track.Artist, track.Album, IpodReader.SafeInt(track.TrackNumber))) == true)
                episodes.Add(track);
        if (episodes.Count == 0) return 0;
        var podcasts = podcastLists.FirstOrDefault(p => p.Name == "Podcasts") ?? ipod.Playlists.Add("Podcasts");
        var playlistWasReclassified = !podcasts.IsPodcastPlaylist;
        podcasts.IsPodcastPlaylist = true;
        var updated = 0;
        foreach (var episode in episodes)
        {
            var changed = Mark(episode) || playlistWasReclassified;
            if (!podcasts.ContainsTrack(episode)) { podcasts.AddTrack(episode); changed = true; }
            foreach (var playlist in playlists)
            {
                if (playlist.IsPodcastPlaylist || playlist == podcasts || !playlist.ContainsTrack(episode)) continue;
                RemovePlaylistEntry.Invoke(playlist, [episode, true]);
                changed = true;
            }
            if (changed) { IpodShuffleFlag.MarkDirty(episode); updated++; }
        }
        return updated;
    }
}
