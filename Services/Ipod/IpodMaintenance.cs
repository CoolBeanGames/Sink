using System.IO;
using Clickwheel;
using Sink.Models;

namespace Sink.Services.Ipod;

public static class IpodMaintenance
{
    /// <summary>Repairs already-synced episodes in place, even when no local audio file remains.</summary>
    public static int RepairPodcastCategories(string root, IReadOnlySet<string> knownEpisodeKeys)
    {
        var ipod = OpenForMaintenance(root);
        ipod.AssertIsWritable();
        string? backup = null;
        var locked = false;
        try
        {
            IPodBackup.EnableBackups = false;
            ipod.AcquireLock(); locked = true;
            var updated = IpodPodcastClassification.Normalize(ipod, knownEpisodeKeys);
            if (updated == 0) return 0;
            backup = RequireBackup(root);
            var expectedIds = new HashSet<int>();
            foreach (var track in ipod.Tracks)
                if (IpodPodcastClassification.IsPodcast(track)) expectedIds.Add(track.Id);
            var expectedCount = ipod.Tracks.Count;
            ipod.SaveChanges(); DriveEject.Flush(root);
            ipod.ReleaseLock(); locked = false;

            var saved = IPod.GetiPodByDrive(new DirectoryInfo(root), IPodLoadAction.NoSync);
            if (saved.Tracks.Count != expectedCount) throw new IOException("The podcast repair changed the device track count.");
            var podcasts = saved.Playlists.GetPlaylistByName("Podcasts");
            if (podcasts is null || !podcasts.IsPodcastPlaylist) throw new IOException("The Podcasts category was not saved correctly.");
            var verified = new HashSet<int>();
            foreach (var track in saved.Tracks)
            {
                if (!expectedIds.Contains(track.Id)) continue;
                if (!IpodPodcastClassification.IsPodcast(track) || !track.RememberPlaybackPosition
                    || !IpodShuffleFlag.Get(track) || !podcasts.ContainsTrack(track))
                    throw new IOException("An episode's podcast settings were not saved correctly.");
                foreach (var playlist in saved.Playlists)
                    if (!playlist.IsPodcastPlaylist && playlist.ContainsTrack(track))
                        throw new IOException("An episode is still present in a music playlist after repair.");
                verified.Add(track.Id);
            }
            if (!expectedIds.SetEquals(verified)) throw new IOException("An episode could not be verified after repair.");
            Log.Info($"iPod podcast repair: fixed {updated} existing episode(s); music membership and shuffle flags verified from disk");
            return updated;
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrEmpty(backup)) IpodWriteService.TryRestore(backup);
            Log.Error("iPod podcast repair failed", ex);
            throw;
        }
        finally { if (locked) ipod.ReleaseLock(); }
    }

    /// <summary>Applies a shuffle preference now and verifies it by reopening the saved database.</summary>
    public static int SetShuffleExclusion(string root, IReadOnlyList<Track> tracks, bool excluded, bool matchDevicePaths)
    {
        if (tracks.Count == 0) return 0;
        var keys = new HashSet<string>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in tracks)
        {
            keys.Add(IpodDbTrack.MakeKey(track.Title, track.Artist, track.Album, track.TrackNumber));
            if (track.LastSyncedKey is not null) keys.Add(track.LastSyncedKey);
            if (matchDevicePaths && !string.IsNullOrWhiteSpace(track.FilePath)) paths.Add(Path.GetFullPath(track.FilePath));
        }
        var ipod = OpenForMaintenance(root); ipod.AssertIsWritable();
        string? backup = null; var locked = false;
        try
        {
            IPodBackup.EnableBackups = false;
            ipod.AcquireLock(); locked = true;
            var expected = new Dictionary<int, bool>();
            var changed = false;
            foreach (var track in ipod.Tracks)
            {
                var matches = matchDevicePaths
                    ? paths.Contains(Path.GetFullPath(IpodReader.ResolvePath(root, track.FilePath)))
                    : keys.Contains(IpodDbTrack.MakeKey(track.Title, track.Artist, track.Album, IpodReader.SafeInt(track.TrackNumber)));
                if (!matches) continue;
                // Episodes must remain out of music shuffle even when an old local preference says otherwise.
                var target = excluded || IpodPodcastClassification.IsPodcast(track);
                if (IpodShuffleFlag.Set(track, target)) changed = true;
                expected[track.Id] = target;
            }
            if (changed) { backup = RequireBackup(root); ipod.SaveChanges(); DriveEject.Flush(root); }
            ipod.ReleaseLock(); locked = false;
            var saved = IPod.GetiPodByDrive(new DirectoryInfo(root), IPodLoadAction.NoSync);
            var verified = new HashSet<int>();
            foreach (var track in saved.Tracks)
                if (expected.TryGetValue(track.Id, out var target))
                {
                    if (IpodShuffleFlag.Get(track) != target) throw new IOException("The iPod did not save the shuffle preference.");
                    verified.Add(track.Id);
                }
            if (verified.Count != expected.Count) throw new IOException("A selected track could not be verified after saving its shuffle preference.");
            Log.Info($"iPod shuffle update: {verified.Count} track(s), excluded={excluded}, verified from disk");
            return verified.Count;
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrEmpty(backup)) IpodWriteService.TryRestore(backup);
            Log.Error("iPod shuffle update failed", ex); throw;
        }
        finally { if (locked) ipod.ReleaseLock(); }
    }

    private static IPod OpenForMaintenance(string root)
    {
        IpodReader.EnsureExtendedSysInfo(root);
        // Do not consume the device's Play Counts file here. The normal reader
        // merges it and preserves resume positions immediately after repair.
        return IPod.GetiPodByDrive(new DirectoryInfo(root), IPodLoadAction.NoSync);
    }

    private static string RequireBackup(string root)
    {
        var backup = IpodWriteService.BackupDatabase(root);
        if (string.IsNullOrEmpty(backup)) throw new IOException("Could not back up the iPod database. No repair was made.");
        return backup;
    }
}
