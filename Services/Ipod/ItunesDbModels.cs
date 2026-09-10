namespace Sink.Services.Ipod;

/// <summary>A track as recorded in the iPod's own iTunesDB.</summary>
public sealed class IpodDbTrack
{
    public uint TrackId { get; set; }
    public ulong DbId { get; set; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string Genre { get; set; } = "";
    public string Composer { get; set; } = "";
    public string Comment { get; set; } = "";
    /// <summary>iPod-relative path, e.g. <c>:iPod_Control:Music:F04:ABCD.mp3</c>.</summary>
    public string IpodPath { get; set; } = "";
    public int TrackNumber { get; set; }
    public int TrackCount { get; set; }
    public int DiscNumber { get; set; }
    public int Year { get; set; }
    public int BitrateKbps { get; set; }
    public long SizeBytes { get; set; }
    public TimeSpan Duration { get; set; }
    public int Rating { get; set; }
    public int PlayCount { get; set; }
    public string FileType { get; set; } = "";

    /// <summary>Resolves <see cref="IpodPath"/> against the mounted iPod root.</summary>
    public string ResolvePath(string ipodRoot)
    {
        var rel = IpodPath.Replace(':', System.IO.Path.DirectorySeparatorChar).TrimStart(System.IO.Path.DirectorySeparatorChar);
        return System.IO.Path.Combine(ipodRoot, rel);
    }
}

public sealed class IpodDbPlaylist
{
    public string Name { get; set; } = "";
    public ulong PlaylistId { get; set; }
    public bool IsMaster { get; set; }
    public List<uint> TrackIds { get; } = [];
}

public sealed class IpodLibrary
{
    public string? DeviceName { get; set; }
    public string? ModelNumber { get; set; }
    public long CapacityBytes { get; set; }
    public string? SerialNumber { get; set; }
    public string? FirmwareVersion { get; set; }
    public List<IpodDbTrack> Tracks { get; } = [];
    public List<IpodDbPlaylist> Playlists { get; } = [];

    public IpodDbTrack? TrackById(uint id) => Tracks.FirstOrDefault(t => t.TrackId == id);
}
