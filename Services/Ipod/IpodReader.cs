using System.IO;
using Clickwheel;
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
        return IPod.GetiPodByDrive(new DirectoryInfo(ipodRoot), IPodLoadAction.NoSync);
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
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "This iPod is missing its SysInfoExtended file. Connect it once in Apple iTunes, " +
                "or run Sink as administrator, to let Sink read the device.", ex);
        }
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, xml);
        }
        catch (IOException) { }
    }

    public static IpodLibrary Read(string ipodRoot)
    {
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

        foreach (var t in ipod.Tracks)
        {
            var file = ResolvePath(ipodRoot, t.FilePath);
            var isPodcast = t.PodcastFlag
                || t.MediaType is CwMediaType.Podcast or CwMediaType.VideoPodcast
                || string.Equals(t.Genre, "Podcast", StringComparison.OrdinalIgnoreCase);
            library.Tracks.Add(new IpodDbTrack
            {
                TrackId = t.Id,
                Title = string.IsNullOrWhiteSpace(t.Title) ? Path.GetFileNameWithoutExtension(file) : t.Title,
                Artist = string.IsNullOrWhiteSpace(t.Artist) ? "Unknown Artist" : t.Artist,
                AlbumArtist = t.AlbumArtist ?? "",
                Album = string.IsNullOrWhiteSpace(t.Album) ? "Unknown Album" : t.Album,
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

        return library;
    }

    private static void ReadSysInfo(string ipodRoot, IpodLibrary library)
    {
        try
        {
            var file = Path.Combine(ipodRoot, "iPod_Control", "Device", "SysInfo");
            if (!File.Exists(file)) return;
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

    private static int SafeInt(uint value) => value > int.MaxValue ? 0 : (int)value;
}
