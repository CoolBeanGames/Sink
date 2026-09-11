namespace Sink.Services.Ipod;

/// <summary>A track as recorded in the iPod's own iTunesDB.</summary>
public sealed class IpodDbTrack
{
    public int TrackId { get; set; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string AlbumArtist { get; set; } = "";
    public string Album { get; set; } = "";
    public string Genre { get; set; } = "";
    public string Comment { get; set; } = "";
    /// <summary>Resolved absolute path to the media file on the mounted iPod.</summary>
    public string FilePath { get; set; } = "";
    public int TrackNumber { get; set; }
    public int DiscNumber { get; set; }
    public int Year { get; set; }
    public int BitrateKbps { get; set; }
    public long SizeBytes { get; set; }
    public TimeSpan Duration { get; set; }
    public int PlayCount { get; set; }
    public bool IsPodcast { get; set; }

    /// <summary>Resume position stored on the device (milliseconds); 0 when none.</summary>
    public long BookmarkMs { get; set; }

    /// <summary>Stable identity used to match against the local library.</summary>
    public string Key => MakeKey(Title, Artist, Album, TrackNumber);

    /// <summary>
    /// Same identity formula, exposed so callers matching against a raw
    /// Clickwheel track (not wrapped in an <see cref="IpodDbTrack"/>) — or a
    /// local <c>Track</c>/podcast episode before it's even been synced — can
    /// compute an identical key without duplicating the format string.
    /// Previously this formula was duplicated inline at each call site; one
    /// copy (here) had picked up invisible U+001F separator characters
    /// between the interpolated fields (present since the file's very first
    /// commit — an authoring artifact, not later tampering) while the other
    /// copies didn't, so the "same" key never actually matched and no music
    /// play count ever reached Reflect (task 154).
    /// </summary>
    public static string MakeKey(string title, string artist, string album, int trackNumber) =>
        $"{title}{artist}{album}{trackNumber}".ToLowerInvariant();
}

public sealed class IpodDbPlaylist
{
    public string Name { get; set; } = "";
    public bool IsMaster { get; set; }
    public bool IsSmart { get; set; }
    public List<int> TrackIds { get; } = [];
}

public sealed class IpodLibrary
{
    public string? DeviceName { get; set; }
    public string? ModelNumber { get; set; }
    public long CapacityBytes { get; set; }
    public long FreeBytes { get; set; }
    public string? SerialNumber { get; set; }
    public List<IpodDbTrack> Tracks { get; } = [];
    public List<IpodDbPlaylist> Playlists { get; } = [];
}
