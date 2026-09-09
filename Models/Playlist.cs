using System.Collections.ObjectModel;

namespace Sink.Models;

public sealed class Playlist
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; set; }
    public ObservableCollection<Guid> TrackIds { get; } = [];
    public string CountText => $"{TrackIds.Count} tracks";
}
