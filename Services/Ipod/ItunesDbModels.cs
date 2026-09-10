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

    /// <summary>Stable identity used to match against the local library.</summary>
    public string Key => $"{Title}{Artist}{Album}{TrackNumber}".ToLowerInvariant();
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
