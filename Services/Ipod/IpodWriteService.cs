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
            var removed = Removed > 0 ? $"Removed {Removed} track{(Removed == 1 ? "" : "s")} from the iPod" : null;
            if (removed is not null && Added == 0 && AlreadyPresent == 0 && Skipped == 0) return removed + ".";
            return (removed is null ? "" : removed + ". ") +
                   $"Synced {Added} track{(Added == 1 ? "" : "s")} to the iPod" +
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

    /// <param name="mirror">
    /// Treat <paramref name="tracks"/> as the complete music library: every
    /// on-device song that doesn't belong to one of them is removed, so the
    /// device ends up matching the library instead of only ever gaining
    /// tracks. Podcast episodes on the device are left to the podcast
    /// reconcile. A library track whose file is temporarily missing still
    /// protects its on-device copy — it just can't be (re)copied.
    /// </param>
    public static IpodSyncResult Sync(
        string root,
        IReadOnlyList<Track> tracks,
        IProgress<(int done, int total, string message)>? progress = null,
        CancellationToken token = default,
        string? deviceId = null,
        bool mirror = false)
    {
        var eligible = tracks.Where(t => IsSyncable(t.FilePath) || IpodTranscoder.CanTranscode(t.FilePath)).ToList();
        var skipped = tracks.Count - eligible.Count;
        if (eligible.Count == 0 && !mirror) return new IpodSyncResult(0, 0, skipped, null);
        Log.Info($"iPod sync: {eligible.Count} eligible track(s), {skipped} skipped, mirror {mirror}, root {root}");

        IPod ipod;
        try { ipod = IpodReader.Open(root); ipod.AssertIsWritable(); }
        catch (Exception ex) { Log.Error("iPod sync: open failed", ex); return new IpodSyncResult(0, 0, skipped, ex.Message); }

        string? backup = null;
        var locked = false;
        int added = 0, present = 0, removed = 0;
        var changedDb = false;
        CwPlaylist? podcastsPlaylist = null;
        try
        {
            backup = BackupDatabase(root);
            IPodBackup.EnableBackups = false;
            ipod.AcquireLock();
            locked = true;

            // Remove first, so space freed by songs deleted from the library
            // is available for the ones being added.
            if (mirror)
            {
                progress?.Report((0, Math.Max(1, eligible.Count), "Removing songs no longer in the library"));
                var keep = LibraryKeys(tracks);
                var orphans = new List<CwTrack>();
                foreach (var t in ipod.Tracks)
                    if (!IsPodcast(t) && !keep.Contains(DeviceKey(t))) orphans.Add(t);
                foreach (var orphan in orphans)
                {
                    token.ThrowIfCancellationRequested();
                    if (ipod.Tracks.Remove(orphan)) removed++;
                }
                if (removed > 0) changedDb = true;
                Log.Info($"iPod sync: mirror removed {removed} of {orphans.Count} on-device song(s) not in the library");
            }

            var byKey = BuildKeyIndex(ipod);
            using var prefetch = new IpodTranscoder.Prefetcher(token);
            for (var i = 0; i < eligible.Count; i++)
            {
                token.ThrowIfCancellationRequested(); // task 129 — Stop syncing button
                var src = eligible[i];
                PrefetchAhead(prefetch, byKey, eligible, i);
                progress?.Report((i, eligible.Count, $"Copying {src.Title}"));
                var currentKey = IpodDbTrack.MakeKey(src.Title, src.Artist, src.Album, src.TrackNumber);
                // A file that has to be converted is matched against the device
                // up front, so a re-sync never re-converts what's already there
                // just to have Add() reject it as a duplicate.
                var onDevice = FindDriftedTrack(byKey, src, currentKey)
                    ?? (IpodTranscoder.NeedsTranscode(src.FilePath) ? byKey.GetValueOrDefault(currentKey) : null);
                if (onDevice is not null)
                {
                    present++;
                    if (ApplyMetadata(onDevice, src)) changedDb = true;
                }
                else
                {
                    try
                    {
                        onDevice = AddTrack(ipod, src, prefetch, progress, i, eligible.Count);
                        if (onDevice is null) { skipped++; continue; }
                        MarkPodcast(onDevice, src);
                        added++;
                        changedDb = true;
                    }
                    catch (TrackAlreadyExistsException existing)
                    {
                        present++;
                        onDevice = existing.ExistingTrack;
                        if (ApplyMetadata(onDevice, src)) changedDb = true;
                    }
                    catch (OutOfDiskSpaceException) { skipped += eligible.Count - i; break; }
                }
                src.LastSyncedKey = currentKey;

                if (PushPlayCount(onDevice, src, deviceId)) changedDb = true;
                if (onDevice is not null && IpodShuffleFlag.Set(onDevice, src.ExcludedFromShuffle)) changedDb = true;

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
            return new IpodSyncResult(added, present, skipped, null, Removed: removed);
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
        CancellationToken token = default,
        string? deviceId = null,
        bool mirror = false)
    {
        var eligible = tracks.Where(t => IsSyncable(t.FilePath) || IpodTranscoder.CanTranscode(t.FilePath)).ToList();
        var skipped = tracks.Count - eligible.Count;
        if (eligible.Count == 0) return new IpodSyncResult(0, 0, skipped, null);
        Log.Info($"iPod playlist sync: \"{playlistName}\", {eligible.Count} eligible track(s), root {root}");
        var members = new HashSet<CwTrack>(ReferenceEqualityComparer.Instance);

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

            var byKey = BuildKeyIndex(ipod);
            using var prefetch = new IpodTranscoder.Prefetcher(token);
            for (var i = 0; i < eligible.Count; i++)
            {
                token.ThrowIfCancellationRequested(); // task 129 — Stop syncing button
                var src = eligible[i];
                PrefetchAhead(prefetch, byKey, eligible, i);
                progress?.Report((i, eligible.Count, $"Copying {src.Title}"));
                var currentKey = IpodDbTrack.MakeKey(src.Title, src.Artist, src.Album, src.TrackNumber);
                // A file that has to be converted is matched against the device
                // up front, so a re-sync never re-converts what's already there
                // just to have Add() reject it as a duplicate.
                var onDevice = FindDriftedTrack(byKey, src, currentKey)
                    ?? (IpodTranscoder.NeedsTranscode(src.FilePath) ? byKey.GetValueOrDefault(currentKey) : null);
                if (onDevice is not null)
                {
                    present++;
                    if (ApplyMetadata(onDevice, src)) changedDb = true;
                }
                else
                {
                    try
                    {
                        onDevice = AddTrack(ipod, src, prefetch, progress, i, eligible.Count);
                        if (onDevice is null) { skipped++; continue; }
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
                        if (ApplyMetadata(onDevice, src)) changedDb = true;
                    }
                    catch (OutOfDiskSpaceException) { skipped += eligible.Count - i; break; }
                }
                src.LastSyncedKey = currentKey;

                if (PushPlayCount(onDevice, src, deviceId)) changedDb = true;
                if (onDevice is not null && IpodShuffleFlag.Set(onDevice, src.ExcludedFromShuffle)) changedDb = true;

                if (onDevice is not null && !playlist.ContainsTrack(onDevice))
                {
                    playlist.AddTrack(onDevice);
                    changedDb = true;
                }
                if (onDevice is not null) members.Add(onDevice);
            }

            // Songs taken out of the library's playlist leave the device's
            // copy of it too — the playlist should match, not only grow.
            // The key check keeps a member whose library track wasn't reached
            // (out of space, file missing) from being dropped.
            if (mirror)
            {
                var keep = LibraryKeys(tracks);
                var stale = new List<CwTrack>();
                for (var j = 0; j < playlist.TrackCount; j++)
                {
                    var member = playlist[j];
                    if (!members.Contains(member) && !keep.Contains(DeviceKey(member))) stale.Add(member);
                }
                foreach (var member in stale) playlist.RemoveTrack(member);
                if (stale.Count > 0)
                {
                    changedDb = true;
                    Log.Info($"iPod playlist sync: removed {stale.Count} track(s) no longer in \"{playlistName}\"");
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

    /// <summary>
    /// Mirror half of a full sync for playlists: deletes every on-device
    /// playlist whose name isn't in <paramref name="keepNames"/> (tracks are
    /// left alone). The master library and the device's Podcasts playlist are
    /// never touched. Returns how many playlists were removed.
    /// </summary>
    public static int RemoveOrphanPlaylists(string root, IReadOnlyCollection<string> keepNames)
    {
        var keep = keepNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        keep.Add("Podcasts");
        IPod ipod;
        try { ipod = IpodReader.Open(root); ipod.AssertIsWritable(); }
        catch (Exception ex) { Log.Error("iPod orphan playlist remove: open failed", ex); return 0; }

        var orphans = new List<CwPlaylist>();
        foreach (var p in ipod.Playlists)
            if (!p.IsMaster && !keep.Contains(p.Name ?? "")) orphans.Add(p);
        if (orphans.Count == 0) return 0;

        string? backup = null;
        var locked = false;
        try
        {
            backup = BackupDatabase(root);
            IPodBackup.EnableBackups = false;
            ipod.AcquireLock();
            locked = true;
            foreach (var playlist in orphans) ipod.Playlists.Remove(playlist, deleteTracks: false);
            ipod.SaveChanges();
            DriveEject.Flush(root);
            Log.Info($"iPod sync: removed {orphans.Count} playlist(s) no longer in the library: {string.Join(", ", orphans.Select(p => p.Name))}");
            return orphans.Count;
        }
        catch (Exception ex)
        {
            Log.Error("iPod orphan playlist remove failed", ex);
            if (!string.IsNullOrEmpty(backup)) TryRestore(backup);
            return 0;
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
    /// Removes on-device tracks matched by identity key (Title+Artist+Album+
    /// TrackNumber, see <see cref="IpodDbTrack.MakeKey"/>) rather than by file
    /// path. Used when a track is deleted from the local library: at that
    /// point the local <c>Track</c> object (and so its <c>FilePath</c>) is
    /// already gone, so <see cref="Remove"/>'s path-based matching — which
    /// needs a live local track to resolve a device path the way "Unsync
    /// from iPod" does — has nothing to compare against.
    /// </summary>
    public static IpodSyncResult RemoveByKey(string root, IReadOnlyCollection<string> keys)
    {
        var targets = keys.Where(k => !string.IsNullOrWhiteSpace(k)).ToHashSet();
        if (targets.Count == 0) return new IpodSyncResult(0, 0, 0, null);

        Log.Info($"iPod remove by key: {targets.Count} target key(s), root {root}");
        IPod ipod;
        try { ipod = IpodReader.Open(root); ipod.AssertIsWritable(); }
        catch (Exception ex) { Log.Error("iPod remove by key: open failed", ex); return new IpodSyncResult(0, 0, 0, ex.Message); }

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
            {
                var key = IpodDbTrack.MakeKey(track.Title, track.Artist, track.Album, IpodReader.SafeInt(track.TrackNumber));
                if (targets.Contains(key)) toRemove.Add(track);
            }
            foreach (var track in toRemove)
                if (ipod.Tracks.Remove(track)) removed++;
            if (removed > 0) { ipod.SaveChanges(); DriveEject.Flush(root); }
            return new IpodSyncResult(0, 0, 0, null, Removed: removed);
        }
        catch (Exception ex)
        {
            Log.Error("iPod remove by key failed", ex);
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
    /// Title+Artist+Album+TrackNumber identity (see <see cref="IpodDbTrack.MakeKey"/>)
    /// is a key in <paramref name="positionsByKey"/>, sets its bookmark. Returns
    /// the number of tracks updated (0 when the DB library can't expose the
    /// bookmark field). Matching used to go by file name, but the device always
    /// renames every synced file to its own opaque hashed name, so that never
    /// matched anything (task 154).
    /// </summary>
    public static int WritePodcastPositions(string root, IReadOnlyDictionary<string, long> positionsByKey)
    {
        if (positionsByKey.Count == 0) return 0;
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
                var key = IpodDbTrack.MakeKey(track.Title, track.Artist, track.Album, IpodReader.SafeInt(track.TrackNumber));
                if (!positionsByKey.TryGetValue(key, out var ms) || ms <= 0) continue;
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
    /// Pushes metadata-only updates — play counts and the shuffle-skip flag —
    /// onto tracks already present on the device, matched by the same
    /// Title/Artist/Album/TrackNumber identity as <see cref="WritePodcastPositions"/>.
    /// Never adds a track or copies a file, so it stays cheap even on a large
    /// library; that's what <see cref="Sync"/>/<see cref="SyncPlaylist"/> are
    /// for. Used by "Sync changes" so toggling exclude-from-shuffle (or any
    /// future in-app play) reaches an already-synced track without a full
    /// re-sync.
    /// </summary>
    public static int PushTrackMetadata(string root, IReadOnlyList<Track> tracks, string? deviceId)
    {
        if (tracks.Count == 0) return 0;
        IPod ipod;
        try { ipod = IpodReader.Open(root); ipod.AssertIsWritable(); }
        catch (Exception) { return 0; }

        var byKey = new Dictionary<string, Track>();
        foreach (var t in tracks)
        {
            var currentKey = IpodDbTrack.MakeKey(t.Title, t.Artist, t.Album, t.TrackNumber);
            byKey.TryAdd(currentKey, t);
            // Also index by the identity as of the last sync, so a track
            // retagged since then is still found under its old on-device
            // identity instead of silently never matching again.
            if (t.LastSyncedKey is not null && t.LastSyncedKey != currentKey) byKey.TryAdd(t.LastSyncedKey, t);
        }
        Log.Info($"iPod metadata push: {tracks.Count} local track(s), {byKey.Count} distinct key(s), root {root}");

        string? backup = null;
        var locked = false;
        var updated = 0;
        var matched = 0;
        try
        {
            backup = BackupDatabase(root);
            IPodBackup.EnableBackups = false;
            ipod.AcquireLock();
            locked = true;

            var changedDb = false;
            foreach (var onDevice in ipod.Tracks)
            {
                var key = IpodDbTrack.MakeKey(onDevice.Title, onDevice.Artist, onDevice.Album, IpodReader.SafeInt(onDevice.TrackNumber));
                if (!byKey.TryGetValue(key, out var src)) continue;
                matched++;
                var changed = false;
                if (ApplyMetadata(onDevice, src)) changed = true;
                if (PushPlayCount(onDevice, src, deviceId)) changed = true;
                if (IpodShuffleFlag.Set(onDevice, src.ExcludedFromShuffle)) changed = true;
                src.LastSyncedKey = IpodDbTrack.MakeKey(src.Title, src.Artist, src.Album, src.TrackNumber);
                if (!changed) continue;
                updated++;
                changedDb = true;
            }
            Log.Info($"iPod metadata push: {matched} on-device track(s) matched by key, {updated} actually changed, changedDb={changedDb}");
            if (changedDb) { ipod.SaveChanges(); DriveEject.Flush(root); }
            return updated;
        }
        catch (Exception ex)
        {
            Log.Error("iPod metadata push failed", ex);
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
    /// <summary>
    /// Pushes Sink's own play count for this track up to the device, so a
    /// song played inside Sink (not on the device itself) shows an updated
    /// count on the iPod after the next sync — previously only the reverse
    /// direction (device plays folding into Sink, see
    /// MainWindow.SyncMusicPlayCountsFromIpod) was ever wired up. Shares the
    /// same SyncedIpodPlayCounts baseline that reverse direction diffs
    /// against, and never lowers whatever the device itself already reports
    /// in case a play happened on-device between this sync's read and write
    /// halves. Returns true if the device's own record actually changed
    /// (contributing to changedDb so this alone still triggers a save).
    /// </summary>
    private static bool PushPlayCount(CwTrack? onDevice, Track src, string? deviceId)
    {
        if (onDevice is null || string.IsNullOrWhiteSpace(deviceId)) return false;
        var target = Math.Max(onDevice.PlayCount, src.PlayCount);
        src.SyncedIpodPlayCounts ??= [];
        src.SyncedIpodPlayCounts[deviceId] = target;
        if (target == onDevice.PlayCount) return false;
        onDevice.PlayCount = target;
        return true;
    }

    private static void MarkPodcast(CwTrack? onDevice, Track src)
    {
        if (onDevice is null || !string.Equals(src.Genre, "Podcast", StringComparison.OrdinalIgnoreCase)) return;
        onDevice.PodcastFlag = true;
        onDevice.MediaType = CwMediaType.Podcast;
    }

    private static string DeviceKey(CwTrack t) =>
        IpodDbTrack.MakeKey(t.Title, t.Artist, t.Album, IpodReader.SafeInt(t.TrackNumber));

    private static bool IsPodcast(CwTrack t) =>
        t.PodcastFlag || t.MediaType == CwMediaType.Podcast ||
        string.Equals(t.Genre, "Podcast", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every identity an on-device copy of these library tracks could carry:
    /// the current one, the file-name title <see cref="NewTrackFrom"/> falls
    /// back to for an untitled track, and the last synced one (a retag not
    /// yet pushed).
    /// </summary>
    private static HashSet<string> LibraryKeys(IEnumerable<Track> tracks)
    {
        var keys = new HashSet<string>();
        foreach (var t in tracks)
        {
            keys.Add(IpodDbTrack.MakeKey(t.Title, t.Artist, t.Album, t.TrackNumber));
            if (string.IsNullOrWhiteSpace(t.Title) && !string.IsNullOrWhiteSpace(t.FilePath))
                keys.Add(IpodDbTrack.MakeKey(Path.GetFileNameWithoutExtension(t.FilePath), t.Artist, t.Album, t.TrackNumber));
            if (t.LastSyncedKey is not null) keys.Add(t.LastSyncedKey);
        }
        return keys;
    }

    /// <summary>Snapshot of every on-device track's current identity, for matching a local track to its existing copy without relying on Clickwheel's own (exact, case-sensitive) Add() dedup.</summary>
    private static Dictionary<string, CwTrack> BuildKeyIndex(IPod ipod)
    {
        var byKey = new Dictionary<string, CwTrack>();
        foreach (var t in ipod.Tracks)
            byKey.TryAdd(IpodDbTrack.MakeKey(t.Title, t.Artist, t.Album, IpodReader.SafeInt(t.TrackNumber)), t);
        return byKey;
    }

    /// <summary>
    /// Resolves the on-device track for a local track about to be synced when
    /// its identity (Title/Artist/Album/TrackNumber) drifted since the last
    /// sync — a retag. Looking it up by its recorded old identity finds the
    /// existing on-device copy so its metadata gets updated in place, instead
    /// of Add()'s own exact-match dedup missing it and silently copying the
    /// file again as a duplicate. Returns null when there's no old identity
    /// to look up (never synced, or nothing changed); callers fall back to
    /// Add()/its TrackAlreadyExistsException dedup as before.
    /// </summary>
    private static CwTrack? FindDriftedTrack(Dictionary<string, CwTrack> byKey, Track src, string currentKey) =>
        src.LastSyncedKey is not null && src.LastSyncedKey != currentKey && byKey.TryGetValue(src.LastSyncedKey, out var stale)
            ? stale
            : null;

    /// <summary>
    /// Pushes Title/Artist/Album/Genre/TrackNumber onto an already-matched
    /// on-device track. Genre isn't part of the identity match key at all, so
    /// even a track found by its unchanged, exact-matching identity could
    /// still have a stale on-device Genre; this catches that too. Returns
    /// true if anything actually changed.
    /// </summary>
    private static bool ApplyMetadata(CwTrack onDevice, Track src)
    {
        var changed = false;
        if (onDevice.Title != src.Title) { onDevice.Title = src.Title; changed = true; }
        if (onDevice.Artist != src.Artist) { onDevice.Artist = src.Artist; changed = true; }
        if (onDevice.Album != src.Album) { onDevice.Album = src.Album; changed = true; }
        if (onDevice.Genre != src.Genre) { onDevice.Genre = src.Genre; changed = true; }
        var trackNumber = (uint)Math.Max(0, src.TrackNumber);
        if (onDevice.TrackNumber != trackNumber) { onDevice.TrackNumber = trackNumber; changed = true; }
        return changed;
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

    /// <summary>
    /// Copies one library track onto the device, converting it first when the
    /// iPod can't play its format (FLAC and friends). Returns null — the track
    /// is skipped — if that conversion fails; the temporary converted file is
    /// deleted as soon as Clickwheel has copied it (Add copies immediately).
    /// </summary>
    private static CwTrack? AddTrack(IPod ipod, Track src, IpodTranscoder.Prefetcher prefetch,
        IProgress<(int done, int total, string message)>? progress, int index, int total)
    {
        string? temp = null;
        try
        {
            var file = src.FilePath!;
            if (IpodTranscoder.NeedsTranscode(file))
            {
                progress?.Report((index, total, $"Converting {src.Title}"));
                try { temp = prefetch.Take(file); }
                catch (InvalidOperationException ex)
                {
                    Log.Warn($"iPod sync: skipped {file} — {ex.Message}");
                    return null;
                }
                progress?.Report((index, total, $"Copying {src.Title}"));
                file = temp;
            }
            return ipod.Tracks.Add(NewTrackFrom(src, file));
        }
        finally { IpodTranscoder.TryDelete(temp); }
    }

    /// <summary>Starts converting the next few tracks that will need it while the current one copies.</summary>
    private static void PrefetchAhead(IpodTranscoder.Prefetcher prefetch, Dictionary<string, CwTrack> byKey, List<Track> eligible, int index)
    {
        for (var j = index; j < Math.Min(eligible.Count, index + 4); j++)
        {
            var t = eligible[j];
            if (!IpodTranscoder.NeedsTranscode(t.FilePath)) continue;
            if (byKey.ContainsKey(IpodDbTrack.MakeKey(t.Title, t.Artist, t.Album, t.TrackNumber))) continue;
            if (t.LastSyncedKey is not null && byKey.ContainsKey(t.LastSyncedKey)) continue;
            prefetch.Queue(t.FilePath!);
        }
    }

    private static NewTrack NewTrackFrom(Track src, string file)
    {
        uint length = (uint)Math.Clamp(src.Duration.TotalMilliseconds, 0, uint.MaxValue);
        uint bitrate = 0;
        try
        {
            using var media = TagLib.File.Create(file);
            if (media.Properties.Duration > TimeSpan.Zero)
                length = (uint)Math.Clamp(media.Properties.Duration.TotalMilliseconds, 0, uint.MaxValue);
            bitrate = (uint)Math.Max(0, media.Properties.AudioBitrate);
        }
        catch { /* fall back to library metadata */ }

        return new NewTrack
        {
            FilePath = file,
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

    internal static string BackupDatabase(string root)
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

    internal static void TryRestore(string backupInfo)
    {
        var parts = backupInfo.Split('|', 2);
        if (parts.Length == 2 && File.Exists(parts[0]))
            try { File.Copy(parts[0], parts[1], true); } catch { }
    }
}
