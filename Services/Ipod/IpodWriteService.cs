using System.IO;
using Clickwheel;
using Clickwheel.Exceptions;
using Sink.Models;
using Sink.Services;
using CwTrack = Clickwheel.Parsers.iTunesDB.Track;
using CwMediaType = Clickwheel.Parsers.iTunesDB.MediaType;
using CwPlaylist = Clickwheel.Parsers.iTunesDB.Playlist;

namespace Sink.Services.Ipod;

public sealed record IpodSyncResult(int Added, int AlreadyPresent, int Skipped, string? Error, int Removed = 0)
{
    public string Summary
    {
        get
        {
            if (Error is not null) return $"iPod update failed — {Error}. The database was restored.";
            if (Removed > 0) return $"Removed {Removed} track{(Removed == 1 ? "" : "s")} from the iPod.";
            return $"Synced {Added} track{(Added == 1 ? "" : "s")} to the iPod" +
                   (AlreadyPresent > 0 ? $", {AlreadyPresent} already there" : "") +
                   (Skipped > 0 ? $", {Skipped} skipped" : "") + ".";
        }
    }
}

/// <summary>
/// Writes to a connected iPod through Clickwheel: copies audio onto the device,
/// adds/removes iTunesDB entries and edits on-device metadata. Clickwheel
/// regenerates the firmware hash on <c>SaveChanges</c>. Every operation takes a
/// database backup first and restores it if anything throws.
/// </summary>
public static class IpodWriteService
{
    private static readonly string[] Supported =
        [".mp3", ".m4a", ".aac", ".wav", ".m4b", ".aa", ".aax"];

    public static bool IsSyncable(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path) &&
        Supported.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static IpodSyncResult Sync(
        string root,
        IReadOnlyList<Track> tracks,
        IProgress<(int done, int total, string message)>? progress = null,
        CancellationToken token = default)
    {
        var eligible = tracks.Where(t => IsSyncable(t.FilePath) && !t.ExcludedFromShuffle).ToList();
        var skipped = tracks.Count - eligible.Count;
        if (eligible.Count == 0) return new IpodSyncResult(0, 0, skipped, null);
        Log.Info($"iPod sync: {eligible.Count} eligible track(s), {skipped} skipped, root {root}");

        IPod ipod;
        try { ipod = IpodReader.Open(root); ipod.AssertIsWritable(); }
        catch (Exception ex) { Log.Error("iPod sync: open failed", ex); return new IpodSyncResult(0, 0, skipped, ex.Message); }

        string? backup = null;
        var locked = false;
        int added = 0, present = 0;
        var changedDb = false;
        CwPlaylist? podcastsPlaylist = null;
        try
        {
            backup = BackupDatabase(root);
            IPodBackup.EnableBackups = false;
            ipod.AcquireLock();
            locked = true;

            for (var i = 0; i < eligible.Count; i++)
            {
                token.ThrowIfCancellationRequested(); // task 129 — Stop syncing button
                var src = eligible[i];
                progress?.Report((i, eligible.Count, $"Copying {src.Title}"));
                CwTrack? onDevice;
                try
                {
                    onDevice = ipod.Tracks.Add(NewTrackFrom(src));
                    MarkPodcast(onDevice, src);
                    added++;
                    changedDb = true;
                }
                catch (TrackAlreadyExistsException existing) { present++; onDevice = existing.ExistingTrack; }
                catch (OutOfDiskSpaceException) { skipped += eligible.Count - i; break; }

                // A track only shows under the device's own Podcasts menu when
                // it's a member of the special "Podcasts" playlist — the
                // PodcastFlag alone (set above) isn't enough (task 147/148).
                if (onDevice is not null && string.Equals(src.Genre, "Podcast", StringComparison.OrdinalIgnoreCase))
                {
                    podcastsPlaylist ??= ipod.Playlists.GetPlaylistByName("Podcasts") ?? ipod.Playlists.Add("Podcasts");
                    if (!podcastsPlaylist.ContainsTrack(onDevice))
                    {
                        podcastsPlaylist.AddTrack(onDevice);
                        changedDb = true;
                    }
                }
            }

            if (changedDb)
            {
                progress?.Report((eligible.Count, eligible.Count, "Updating the iPod database"));
                ipod.SaveChanges();
                DriveEject.Flush(root); // force the write out of the OS cache — a quick eject right after used to lose it (task 144)
            }
            return new IpodSyncResult(added, present, skipped, null);
        }
        catch (OperationCanceledException)
        {
            Log.Info("iPod sync cancelled");
            if (!string.IsNullOrEmpty(backup)) TryRestore(backup);
            throw;
        }
        catch (Exception ex)
        {
            Log.Error("iPod sync failed", ex);
            if (!string.IsNullOrEmpty(backup)) TryRestore(backup);
            return new IpodSyncResult(added, present, skipped, ex.Message);
        }
        finally
        {
            if (locked) { try { ipod.ReleaseLock(); } catch { } }
        }
    }

    /// <summary>
    /// Syncs a playlist's tracks to the iPod (same as <see cref="Sync"/>) and also
    /// creates/updates a same-named on-device playlist containing them, so it
    /// shows up as a playlist in iTunes/on the device rather than just loose
    /// tracks in the library.
    /// </summary>
    public static IpodSyncResult SyncPlaylist(
        string root,
        string playlistName,
        IReadOnlyList<Track> tracks,
        IProgress<(int done, int total, string message)>? progress = null,
        CancellationToken token = default)
    {
        var eligible = tracks.Where(t => IsSyncable(t.FilePath) && !t.ExcludedFromShuffle).ToList();
        var skipped = tracks.Count - eligible.Count;
        if (eligible.Count == 0) return new IpodSyncResult(0, 0, skipped, null);
        Log.Info($"iPod playlist sync: \"{playlistName}\", {eligible.Count} eligible track(s), root {root}");

        IPod ipod;
        try { ipod = IpodReader.Open(root); ipod.AssertIsWritable(); }
        catch (Exception ex) { Log.Error("iPod playlist sync: open failed", ex); return new IpodSyncResult(0, 0, skipped, ex.Message); }

        string? backup = null;
        var locked = false;
        int added = 0, present = 0;
        var changedDb = false;
        try
        {
            backup = BackupDatabase(root);
            IPodBackup.EnableBackups = false;
            ipod.AcquireLock();
            locked = true;

            var playlist = ipod.Playlists.GetPlaylistByName(playlistName);
            var isNewPlaylist = playlist is null;
            playlist ??= ipod.Playlists.Add(playlistName);
            Log.Info($"iPod playlist sync: \"{playlistName}\" {(isNewPlaylist ? "created" : "found existing")}, had {playlist.TrackCount} track(s), {ipod.Playlists.Count} playlist(s) total on device");

            for (var i = 0; i < eligible.Count; i++)
            {
                token.ThrowIfCancellationRequested(); // task 129 — Stop syncing button
                var src = eligible[i];
                progress?.Report((i, eligible.Count, $"Copying {src.Title}"));
                CwTrack? onDevice;
                try
                {
                    onDevice = ipod.Tracks.Add(NewTrackFrom(src));
                    MarkPodcast(onDevice, src);
                    added++;
                    changedDb = true;
                }
                catch (TrackAlreadyExistsException existing)
                {
                    // Clickwheel rewrites a track's FilePath to its on-device
                    // location the moment it's copied, so re-deriving "the
                    // existing track" by comparing that resolved on-device
                    // path against the original source path could never
                    // match — it was comparing D:\iPod_Control\Music\... to
                    // Z:\Sink\Music\... and always came up empty. The
                    // exception already carries the real match directly
                    // (Clickwheel dedupes by title/artist/album/track number,
                    // not by path at all). Every song already synced before
                    // adding it to a playlist hit this path, so a playlist
                    // whose tracks were already on the device (the normal
                    // case) never got any track linked to it, changedDb
                    // stayed false, and the whole playlist silently never
                    // reached SaveChanges (task 141).
                    present++;
                    onDevice = existing.ExistingTrack;
                }
                catch (OutOfDiskSpaceException) { skipped += eligible.Count - i; break; }

                if (onDevice is not null && !playlist.ContainsTrack(onDevice))
                {
                    playlist.AddTrack(onDevice);
                    changedDb = true;
                }
            }

            if (changedDb)
            {
                progress?.Report((eligible.Count, eligible.Count, "Updating the iPod database"));
                Log.Info($"iPod playlist sync: \"{playlistName}\" has {playlist.TrackCount} track(s) in memory before SaveChanges");
                ipod.SaveChanges();
                DriveEject.Flush(root);
                VerifyPlaylistPersisted(root, playlistName, playlist.TrackCount);
            }
            return new IpodSyncResult(added, present, skipped, null);
        }
        catch (OperationCanceledException)
        {
            Log.Info("iPod playlist sync cancelled");
            if (!string.IsNullOrEmpty(backup)) TryRestore(backup);
            throw;
        }
        catch (Exception ex)
        {
            Log.Error("iPod playlist sync failed", ex);
            if (!string.IsNullOrEmpty(backup)) TryRestore(backup);
            return new IpodSyncResult(added, present, skipped, ex.Message);
        }
        finally
        {
            if (locked) { try { ipod.ReleaseLock(); } catch { } }
        }
    }

    /// <summary>Removes just the named playlist from the device — its tracks are left alone, matching how "delete playlist" already works for the library's own playlists (task 134).</summary>
    public static bool RemovePlaylist(string root, string playlistName)
    {
        Log.Info($"iPod playlist remove: \"{playlistName}\", root {root}");
        IPod ipod;
        try { ipod = IpodReader.Open(root); ipod.AssertIsWritable(); }
        catch (Exception ex) { Log.Error("iPod playlist remove: open failed", ex); return false; }

        string? backup = null;
        var locked = false;
        try
        {
            var playlist = ipod.Playlists.GetPlaylistByName(playlistName);
            if (playlist is null || playlist.IsMaster) return false;

            backup = BackupDatabase(root);
            IPodBackup.EnableBackups = false;
            ipod.AcquireLock();
            locked = true;

            ipod.Playlists.Remove(playlist, deleteTracks: false);
            ipod.SaveChanges();
            DriveEject.Flush(root);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("iPod playlist remove failed", ex);
            if (!string.IsNullOrEmpty(backup)) TryRestore(backup);
            return false;
        }
        finally
        {
            if (locked) { try { ipod.ReleaseLock(); } catch { } }
        }
    }

    public static IpodSyncResult Remove(string root, IReadOnlyCollection<string> absoluteFilePaths)
    {
        var targets = absoluteFilePaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (targets.Count == 0) return new IpodSyncResult(0, 0, 0, null);

        Log.Info($"iPod remove: {targets.Count} target file(s), root {root}");
        IPod ipod;
        try { ipod = IpodReader.Open(root); ipod.AssertIsWritable(); }
        catch (Exception ex) { Log.Error("iPod remove: open failed", ex); return new IpodSyncResult(0, 0, 0, ex.Message); }

        string? backup = null;
        var locked = false;
        var removed = 0;
        try
        {
            backup = BackupDatabase(root);
            IPodBackup.EnableBackups = false;
            ipod.AcquireLock();
            locked = true;

            var toRemove = new List<CwTrack>();
            foreach (var track in ipod.Tracks)
                if (targets.Contains(Path.GetFullPath(IpodReader.ResolvePath(root, track.FilePath))))
                    toRemove.Add(track);
            foreach (var track in toRemove)
                if (ipod.Tracks.Remove(track)) removed++;
            if (removed > 0) { ipod.SaveChanges(); DriveEject.Flush(root); }
            return new IpodSyncResult(0, 0, 0, null, Removed: removed);
        }
        catch (Exception ex)
        {
            Log.Error("iPod remove failed", ex);
            if (!string.IsNullOrEmpty(backup)) TryRestore(backup);
            return new IpodSyncResult(0, 0, 0, ex.Message);
        }
        finally
        {
            if (locked) { try { ipod.ReleaseLock(); } catch { } }
        }
    }

    /// <summary>
    /// Pushes podcast resume positions onto the device: for each iPod track whose
    /// file name is a key in <paramref name="positionsByFileName"/>, sets its
    /// bookmark. Returns the number of tracks updated (0 when the DB library
    /// can't expose the bookmark field).
    /// </summary>
    public static int WritePodcastPositions(string root, IReadOnlyDictionary<string, long> positionsByFileName)
    {
        if (positionsByFileName.Count == 0) return 0;
        IPod ipod;
        try { ipod = IpodReader.Open(root); ipod.AssertIsWritable(); }
        catch (Exception) { return 0; }

        string? backup = null;
        var locked = false;
        var updated = 0;
        try
        {
            backup = BackupDatabase(root);
            IPodBackup.EnableBackups = false;
            ipod.AcquireLock();
            locked = true;

            foreach (var track in ipod.Tracks)
            {
                var name = Path.GetFileName(IpodReader.ResolvePath(root, track.FilePath));
                if (!positionsByFileName.TryGetValue(name, out var ms) || ms <= 0) continue;
                if (IpodBookmarks.GetMs(track) >= ms) continue; // device already further along
                IpodBookmarks.SetMs(track, ms);
                updated++;
            }
            if (updated > 0) { ipod.SaveChanges(); DriveEject.Flush(root); }
            return updated;
        }
        catch (Exception ex)
        {
            Log.Error("iPod podcast-position write failed", ex);
            if (!string.IsNullOrEmpty(backup)) TryRestore(backup);
            return 0;
        }
        finally
        {
            if (locked) { try { ipod.ReleaseLock(); } catch { } }
        }
    }

    /// <summary>
    /// A podcast episode's on-device visibility depends on the firmware-level
    /// PodcastFlag/MediaType bit, not the Genre string — the device's own
    /// Podcasts menu (unlike Sink's own reader, see IpodReader.Read) never
    /// looks at Genre. NewTrack has no such field, so a Sink-synced episode
    /// landed as an indistinguishable, invisible-under-Podcasts plain Music
    /// track even though the write itself fully succeeded (task 140/146).
    /// </summary>
    private static void MarkPodcast(CwTrack? onDevice, Track src)
    {
        if (onDevice is null || !string.Equals(src.Genre, "Podcast", StringComparison.OrdinalIgnoreCase)) return;
        onDevice.PodcastFlag = true;
        onDevice.MediaType = CwMediaType.Podcast;
    }

    /// <summary>
    /// Diagnostic only (task: playlist sync reports success but the playlist
    /// doesn't appear on-device while its tracks do — narrowing down whether
    /// that's a Sink/Clickwheel write bug or a device firmware display quirk).
    /// Re-opens the database fresh from disk — a separate IPod instance, not
    /// the one still in memory — and checks whether the playlist survived.
    /// </summary>
    private static void VerifyPlaylistPersisted(string root, string playlistName, int expectedTrackCount)
    {
        try
        {
            var reopened = IpodReader.Open(root);
            var found = reopened.Playlists.GetPlaylistByName(playlistName);
            Log.Info(found is null
                ? $"iPod playlist sync: VERIFY FAILED — \"{playlistName}\" not found on a fresh re-read of the database ({reopened.Playlists.Count} playlist(s) total)"
                : $"iPod playlist sync: verified — \"{playlistName}\" persisted with {found.TrackCount} track(s) (expected {expectedTrackCount})");
        }
        catch (Exception ex) { Log.Warn($"iPod playlist sync: verify re-read failed: {ex.Message}"); }
    }

    private static NewTrack NewTrackFrom(Track src)
    {
        uint length = (uint)Math.Clamp(src.Duration.TotalMilliseconds, 0, uint.MaxValue);
        uint bitrate = 0;
        try
        {
            using var media = TagLib.File.Create(src.FilePath);
            if (media.Properties.Duration > TimeSpan.Zero)
                length = (uint)Math.Clamp(media.Properties.Duration.TotalMilliseconds, 0, uint.MaxValue);
            bitrate = (uint)Math.Max(0, media.Properties.AudioBitrate);
        }
        catch { /* fall back to library metadata */ }

        return new NewTrack
        {
            FilePath = src.FilePath!,
            Title = string.IsNullOrWhiteSpace(src.Title) ? Path.GetFileNameWithoutExtension(src.FilePath) : src.Title,
            Artist = src.Artist,
            AlbumArtist = src.Artist,
            Album = src.Album,
            Genre = src.Genre,
            Composer = "",
            Comments = "",
            TrackNumber = (uint)Math.Max(0, src.TrackNumber),
            AlbumTrackCount = 0,
            DiscNumber = 1,
            TotalDiscCount = 1,
            Year = (uint)Math.Max(0, src.Year),
            Length = length,
            Bitrate = bitrate,
            IsVideo = false,
            ArtworkFile = null,
        };
    }

    private static string BackupDatabase(string root)
    {
        var iTunes = Path.Combine(root, "iPod_Control", "iTunes");
        var source = Path.Combine(iTunes, "iTunesDB");
        if (!File.Exists(source)) source = Path.Combine(iTunes, "iTunesCDB");
        var dir = Path.Combine(LibraryStore.Directory, "ipod-backups");
        Directory.CreateDirectory(dir);
        var backup = Path.Combine(dir, $"{Path.GetFileName(source)}-{DateTime.Now:yyyyMMdd-HHmmssfff}.backup");
        try
        {
            File.Copy(source, backup, true);
            foreach (var old in new DirectoryInfo(dir).GetFiles("*.backup").OrderByDescending(f => f.CreationTimeUtc).Skip(10))
                try { old.Delete(); } catch { }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
        return backup + "|" + source;
    }

    private static void TryRestore(string backupInfo)
    {
        var parts = backupInfo.Split('|', 2);
        if (parts.Length == 2 && File.Exists(parts[0]))
            try { File.Copy(parts[0], parts[1], true); } catch { }
    }
}
