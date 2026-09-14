using System.Diagnostics;
using System.IO;
using Clickwheel;
using Sink.Services;
using CwMediaType = Clickwheel.Parsers.iTunesDB.MediaType;

namespace Sink.Services.Ipod;

/// <summary>
/// Reads a mounted iPod's library through the Clickwheel iTunesDB library — the
/// same engine iTunes-compatible tools use to parse (and later write) the
/// database and its firmware hash.
/// </summary>
public static class IpodReader
{
    public static bool HasDatabase(string ipodRoot)
    {
        var iTunes = Path.Combine(ipodRoot, "iPod_Control", "iTunes");
        return File.Exists(Path.Combine(iTunes, "iTunesDB")) || File.Exists(Path.Combine(iTunes, "iTunesCDB"));
    }

    internal static IPod Open(string ipodRoot)
    {
        EnsureExtendedSysInfo(ipodRoot);
        // SyncPlayCounts (not NoSync) — root cause of listen counts never
        // syncing back (task: "Still not syncing play data" and predecessors).
        // Classic iPod firmware doesn't write play/skip counts into the main
        // iTunesDB during normal use; it accumulates them in a separate
        // "iPod_Control/iTunes/Play Counts" file. Clickwheel only merges that
        // file into Track.PlayCount when opened with this load action — with
        // NoSync, PlayCount only ever reflected whatever was last written by
        // a sync, frozen at 0 for anything not freshly added, no matter how
        // much was actually played on the device. Confirmed by decompiling
        // Clickwheel 0.1.1 (IPod's constructor, PlayCounts.MergeChanges) and
        // cross-checked against a prior working sibling app (htunes) that
        // already opens this way. The merge also unconditionally *deletes*
        // the on-device Play Counts file as its last step — see Read()'s
        // persist-back step, which is what makes that safe to do here.
        return IPod.GetiPodByDrive(new DirectoryInfo(ipodRoot), IPodLoadAction.SyncPlayCounts);
    }

    /// <summary>
    /// Hand-parses "iPod_Control/iTunes/Play Counts" directly (the "mhdp"
    /// format) for its per-entry resume-position field, positionally aligned
    /// with the device's track list — Clickwheel's own PlayCounts.MergeChanges
    /// only ever pulls PlayCount and Rating out of this file, never position,
    /// so this is the only way to recover it. Must be called before Open()
    /// deletes the file. Same layout a prior sibling app (htunes) already
    /// reads this file with.
    /// </summary>
    private static IReadOnlyList<long> ReadRawBookmarkPositions(string ipodRoot)
    {
        try
        {
            var path = Path.Combine(ipodRoot, "iPod_Control", "iTunes", "Play Counts");
            if (!File.Exists(path)) return [];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 16 || new string(reader.ReadChars(4)) != "mhdp") return [];
            var headerSize = reader.ReadInt32();
            var entrySize = reader.ReadInt32();
            var entryCount = reader.ReadInt32();
            if (headerSize < 16 || entrySize < 12 || entryCount < 0 || headerSize + (long)entrySize * entryCount > stream.Length)
                return [];
            stream.Position = headerSize;
            var result = new List<long>(entryCount);
            for (var index = 0; index < entryCount; index++)
            {
                var entryStart = stream.Position;
                _ = reader.ReadInt32();  // play count (already covered by Clickwheel's own merge)
                _ = reader.ReadUInt32(); // last-played timestamp
                result.Add(Math.Max(0, reader.ReadInt32())); // bookmark position, milliseconds
                stream.Position = entryStart + entrySize;
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return [];
        }
    }

    /// <summary>
    /// Clickwheel needs <c>iPod_Control/Device/SysInfoExtended</c>. iTunes writes
    /// it, so a managed iPod already has one; otherwise generate it by asking the
    /// drive directly over SCSI (this is what makes a fresh iPod usable).
    /// </summary>
    public static void EnsureExtendedSysInfo(string ipodRoot)
    {
        var dir = Path.Combine(ipodRoot, "iPod_Control", "Device");
        var path = Path.Combine(dir, "SysInfoExtended");
        try { if (File.Exists(path) && new FileInfo(path).Length > 0) return; }
        catch (IOException) { return; }

        string xml;
        try { xml = DeviceSysInfoReader.Read(ipodRoot); }
        catch (Exception directEx)
        {
            // The SCSI INQUIRY this needs is routinely denied to a non-elevated
            // process even though opening the physical drive handle itself
            // succeeds — a prior, confirmed-working iteration of this app
            // (hTunes) always did this step via a one-shot elevated relaunch
            // rather than in-process for exactly that reason. Do the same
            // instead of leaving every non-admin launch unable to write a
            // usable database to a fresh/never-elevated iPod.
            if (!TryGenerateElevated(ipodRoot, out var elevatedEx))
            {
                throw new InvalidOperationException(
                    "This iPod needs a one-time administrator permission to read its device info. " +
                    "Accept the prompt, connect it once in Apple iTunes, or run Sink as administrator.",
                    elevatedEx ?? directEx);
            }
            return;
        }
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, xml);
        }
        catch (IOException) { }
    }

    private static bool TryGenerateElevated(string ipodRoot, out Exception? error)
    {
        error = null;
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) return false;
        var start = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas" };
        start.ArgumentList.Add("--prepare-ipod");
        start.ArgumentList.Add(ipodRoot);
        try
        {
            using var process = Process.Start(start);
            process?.WaitForExit();
            var path = Path.Combine(ipodRoot, "iPod_Control", "Device", "SysInfoExtended");
            return process?.ExitCode == 0 && File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch (Exception ex)
        {
            error = ex; // includes Win32Exception code 1223 when the UAC prompt is declined
            return false;
        }
    }

    public static IpodLibrary Read(string ipodRoot)
    {
        // Must happen before Open() — opening with SyncPlayCounts (see Open)
        // deletes this same file as a side effect. Clickwheel's own merge
        // only carries PlayCount/Rating out of it, never position, even
        // though the file has one (confirmed against htunes, a prior sibling
        // app that already reads this file directly for exactly this reason)
        // — so this is the only way to recover a device-updated resume
        // position that was never explicitly pushed by Sink itself.
        var rawBookmarks = ReadRawBookmarkPositions(ipodRoot);
        var ipod = Open(ipodRoot);
        var library = new IpodLibrary();

        try
        {
            var drive = new DriveInfo(ipodRoot);
            library.CapacityBytes = drive.TotalSize;
            library.FreeBytes = drive.AvailableFreeSpace;
            library.DeviceName = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? null : drive.VolumeLabel.Trim();
        }
        catch (IOException) { }

        ReadSysInfo(ipodRoot, library);

        var rawPlayCounts = new List<(bool isPodcast, int raw)>();
        var trackIndex = 0;
        foreach (var t in ipod.Tracks)
        {
            var file = ResolvePath(ipodRoot, t.FilePath);
            var isPodcast = t.PodcastFlag
                || t.MediaType is CwMediaType.Podcast or CwMediaType.VideoPodcast
                || string.Equals(t.Genre, "Podcast", StringComparison.OrdinalIgnoreCase);
            rawPlayCounts.Add((isPodcast, t.PlayCount)); // pre-clamp — see the distribution log below
            // Play Counts entries line up positionally with ipod.Tracks, not
            // by any key (same assumption the prior sibling app makes) — take
            // whichever position is further along between this and the
            // track's own _bookmarkTime (which does hold a real value when
            // Sink itself pushed one, just not when the device updated it).
            var rawBookmarkMs = trackIndex < rawBookmarks.Count ? rawBookmarks[trackIndex] : 0;
            trackIndex++;
            library.Tracks.Add(new IpodDbTrack
            {
                TrackId = t.Id,
                Title = string.IsNullOrWhiteSpace(t.Title) ? Path.GetFileNameWithoutExtension(file) : t.Title,
                // Deliberately NOT substituted to "Unknown Artist"/"Unknown
                // Album" here (unlike Title/Genre) — this feeds Key, used to
                // match a device track back against the local library
                // (Reflect play counts, podcast status). AdaptIpodTrack
                // already substitutes independently for anything shown in
                // the UI, so raw values here cost nothing — but substituting
                // here too risked a real device track (whatever it actually
                // is) silently keying as "unknown artist"/"unknown album"
                // while the local Track kept its real, non-blank name,
                // guaranteeing that pair could never match.
                Artist = t.Artist ?? "",
                AlbumArtist = t.AlbumArtist ?? "",
                Album = t.Album ?? "",
                Genre = string.IsNullOrWhiteSpace(t.Genre) ? "Unknown" : t.Genre,
                Comment = t.Comment ?? "",
                FilePath = file,
                TrackNumber = SafeInt(t.TrackNumber),
                DiscNumber = Math.Max(1, SafeInt(t.DiscNumber)),
                Year = SafeInt(t.Year),
                BitrateKbps = SafeInt(t.Bitrate),
                SizeBytes = t.FileSize.ByteCount,
                Duration = TimeSpan.FromMilliseconds(Math.Max(0, t.Length.MilliSeconds)),
                PlayCount = Math.Max(0, t.PlayCount),
                IsPodcast = isPodcast,
                BookmarkMs = Math.Max(IpodBookmarks.GetMs(t), rawBookmarkMs),
            });
        }

        var byId = library.Tracks.ToDictionary(t => t.TrackId);
        foreach (var p in ipod.Playlists)
        {
            var view = new IpodDbPlaylist { Name = p.Name, IsMaster = p.IsMaster, IsSmart = p.IsSmartPlaylist };
            foreach (var track in p.Tracks)
                if (byId.ContainsKey(track.Id)) view.TrackIds.Add(track.Id);
            library.Playlists.Add(view);
        }

        // One-time distribution check across the whole device on every read,
        // using the RAW pre-clamp PlayCount (Math.Max(0, ...) above would
        // silently turn a negative sentinel value into an indistinguishable
        // 0) — logged unconditionally, not just when something looks wrong,
        // because direct evidence straight from the field beats another
        // inferred fix after 3+ rounds of guessing at this.
        var music = rawPlayCounts.Where(r => !r.isPodcast).ToList();
        var podcasts = rawPlayCounts.Where(r => r.isPodcast).ToList();
        var bookmarked = library.Tracks.Count(t => t.BookmarkMs > 0);
        Log.Info($"iPod read: {music.Count} music track(s) — raw PlayCount: >0 count={music.Count(r => r.raw > 0)}, <0 count={music.Count(r => r.raw < 0)}, max={(music.Count > 0 ? music.Max(r => r.raw) : 0)}, min={(music.Count > 0 ? music.Min(r => r.raw) : 0)}");
        Log.Info($"iPod read: {podcasts.Count} podcast track(s) — raw PlayCount: >0 count={podcasts.Count(r => r.raw > 0)}, <0 count={podcasts.Count(r => r.raw < 0)}, max={(podcasts.Count > 0 ? podcasts.Max(r => r.raw) : 0)}, min={(podcasts.Count > 0 ? podcasts.Min(r => r.raw) : 0)}");
        Log.Info($"iPod read: {bookmarked} of {library.Tracks.Count} total track(s) have BookmarkMs > 0");

        PersistMergedPlayCounts(ipod, ipodRoot);
        return library;
    }

    /// <summary>
    /// Opening with SyncPlayCounts (see Open) merges the device's Play Counts
    /// file into these Track objects in memory and unconditionally deletes
    /// that file from the device as its very last step — regardless of
    /// whether anything is ever saved. A plain read that never persisted
    /// this back would decode that file's real listening data exactly once,
    /// then permanently lose it the moment the file was gone: the next read
    /// would fall back to whatever the main database alone says (frozen at 0
    /// for anything not freshly synced). Same backup-before/restore-on-failure
    /// safety net every write in IpodWriteService already uses. Soft-fails —
    /// a read that succeeded still returns its (correctly merged, in-memory)
    /// data even if persisting the merge back doesn't succeed.
    /// </summary>
    private static void PersistMergedPlayCounts(IPod ipod, string root)
    {
        if (!ipod.IsWritable) return;
        string? backup = null;
        var locked = false;
        try
        {
            backup = IpodWriteService.BackupDatabase(root);
            ipod.AcquireLock();
            locked = true;
            ipod.SaveChanges();
            DriveEject.Flush(root); // same as every other write here — don't leave it sitting in the OS write cache if the device gets unplugged right after
        }
        catch (Exception ex)
        {
            Log.Warn($"iPod read: couldn't persist merged play counts back to the device: {ex.Message}");
            if (!string.IsNullOrEmpty(backup)) IpodWriteService.TryRestore(backup);
        }
        finally
        {
            if (locked) { try { ipod.ReleaseLock(); } catch { } }
        }
    }

    private static void ReadSysInfo(string ipodRoot, IpodLibrary library)
    {
        try
        {
            var file = Path.Combine(ipodRoot, "iPod_Control", "Device", "SysInfo");
            if (File.Exists(file))
                foreach (var line in File.ReadAllLines(file))
                {
                    var idx = line.IndexOf(':');
                    if (idx <= 0) continue;
                    var key = line[..idx].Trim();
                    var value = line[(idx + 1)..].Trim();
                    if (key == "ModelNumStr") library.ModelNumber = value.TrimStart('x', 'X');
                    else if (key == "pszSerialNumber") library.SerialNumber = value;
                }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        if (!string.IsNullOrWhiteSpace(library.SerialNumber)) return;
        // The legacy SysInfo file above is empty on at least one real device
        // (confirmed while diagnosing task 154) — with no serial number,
        // SyncMusicPlayCountsFromIpod had nothing to baseline against and
        // silently skipped every device, so no play count ever reached
        // Reflect. SysInfoExtended (the plist Clickwheel itself requires for
        // database hashing — see EnsureExtendedSysInfo) carries the same
        // serial under its own SerialNumber key, so fall back to that.
        try
        {
            var extended = Path.Combine(ipodRoot, "iPod_Control", "Device", "SysInfoExtended");
            if (!File.Exists(extended)) return;
            var match = System.Text.RegularExpressions.Regex.Match(
                File.ReadAllText(extended), @"<key>SerialNumber</key>\s*<string>([^<]+)</string>");
            if (match.Success) library.SerialNumber = match.Groups[1].Value.Trim();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static string ResolvePath(string ipodRoot, string storedPath)
    {
        if (Path.IsPathFullyQualified(storedPath)) return storedPath;
        var relative = storedPath
            .Replace(':', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.Combine(ipodRoot, relative);
    }

    internal static int SafeInt(uint value) => value > int.MaxValue ? 0 : (int)value;
}
