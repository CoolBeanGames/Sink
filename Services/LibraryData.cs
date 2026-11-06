using Sink.Models;

namespace Sink.Services;

public sealed class PlaylistData
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Playlist";
    public List<Guid> TrackIds { get; set; } = [];
}

public sealed class LibraryData
{
    public List<Track> Tracks { get; set; } = [];
    public List<PlaylistData> Playlists { get; set; } = [];
    public List<Guid> SyncedTrackIds { get; set; } = [];
}
