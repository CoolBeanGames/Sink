using Sink.Models;

namespace Sink.Services.LanSync;

internal static class LanProjection
{
    public static LanSnapshot Capture(LanLocalData local, string musicRoot, string podcastRoot, string originalRoot)
    {
        var portable = new LanSnapshot
        {
            Tracks = local.Library.Tracks,
            Playlists = local.Library.Playlists,
            Podcasts = local.Podcasts,
            Listens = local.Listens,
        }.Copy();
        foreach (var track in portable.Tracks)
        {
            track.FilePath = LanPaths.Relative(musicRoot, track.FilePath);
            track.OriginalPath = LanPaths.Relative(originalRoot, track.OriginalPath);
        }
        foreach (var show in portable.Podcasts)
        {
            show.ArtworkUrl = LanSnapshot.WebUrl(show.ArtworkUrl);
            show.FeedUrl = LanSnapshot.WebUrl(show.FeedUrl) ?? "";
            foreach (var episode in show.Episodes)
            {
                episode.LocalPath = LanPaths.Relative(podcastRoot, episode.LocalPath);
                episode.AudioUrl = LanSnapshot.WebUrl(episode.AudioUrl) ?? "";
                episode.IsNew = false;
                episode.IpodBookmarkMs = 0;
            }
        }
        portable.Validate();
        return portable;
    }

    public static LanLocalData Restore(LanSnapshot portable, LanLocalData local, string musicRoot, string podcastRoot, string originalRoot)
    {
        var incoming = portable.Copy();
        incoming.Validate();
        var tracksById = local.Library.Tracks.ToDictionary(t => t.Id);
        var tracksByPath = local.Library.Tracks.ToLookup(t => LanPaths.Relative(musicRoot, t.FilePath), StringComparer.OrdinalIgnoreCase);
        var synced = local.Library.SyncedTrackIds.ToHashSet();
        var nextSynced = new List<Guid>();
        foreach (var track in incoming.Tracks)
        {
            var previous = tracksById.GetValueOrDefault(track.Id);
            if (previous is null && track.FilePath is { } path && tracksByPath[path].Count() == 1)
                previous = tracksByPath[path].Single();
            track.FilePath = Resolve(musicRoot, track.FilePath, previous?.FilePath);
            track.OriginalPath = Resolve(originalRoot, track.OriginalPath, previous?.OriginalPath);
            if (previous is null) continue;
            track.ArtworkPath = previous.ArtworkPath;
            track.LastSyncedKey = previous.LastSyncedKey;
            track.SyncedIpodPlayCounts = previous.SyncedIpodPlayCounts;
            if (string.Equals(previous.FilePath, track.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                track.QualityVerdict = previous.QualityVerdict;
                track.QualityCheckedPath = previous.QualityCheckedPath;
            }
            if (synced.Contains(previous.Id)) nextSynced.Add(track.Id);
        }
        var knownTracks = incoming.Tracks.Select(t => t.Id).ToHashSet();
        foreach (var playlist in incoming.Playlists)
            playlist.TrackIds = playlist.TrackIds.Where(knownTracks.Contains).Distinct().ToList();

        foreach (var show in incoming.Podcasts)
        {
            var previous = local.Podcasts.FirstOrDefault(p => p.Id == show.Id)
                ?? (show.FeedUrl.Length > 0 ? local.Podcasts.FirstOrDefault(p => p.FeedUrl.Equals(show.FeedUrl, StringComparison.OrdinalIgnoreCase)) : null);
            var byId = previous?.Episodes.ToDictionary(e => e.Id) ?? [];
            var byGuid = (previous?.Episodes ?? []).ToLookup(e => e.EpisodeGuid);
            var byPath = (previous?.Episodes ?? []).ToLookup(e => LanPaths.Relative(podcastRoot, e.LocalPath), StringComparer.OrdinalIgnoreCase);
            foreach (var episode in show.Episodes)
            {
                var old = byId.GetValueOrDefault(episode.Id);
                if (old is null && episode.EpisodeGuid.Length > 0 && byGuid[episode.EpisodeGuid].Count() == 1)
                    old = byGuid[episode.EpisodeGuid].Single();
                if (old is null && episode.LocalPath is { } path && byPath[path].Count() == 1)
                    old = byPath[path].Single();
                episode.LocalPath = Resolve(podcastRoot, episode.LocalPath, old?.LocalPath);
                episode.IsNew = old?.IsNew ?? false;
                episode.IpodBookmarkMs = old?.IpodBookmarkMs ?? 0;
            }
        }
        return new(new LibraryData { Tracks = incoming.Tracks, Playlists = incoming.Playlists, SyncedTrackIds = nextSynced },
            incoming.Podcasts, incoming.Listens);
    }

    private static string? Resolve(string root, string? relative, string? old) =>
        LanPaths.Resolve(root, relative) ?? (LanPaths.Relative(root, old) is null ? old : null);
}
