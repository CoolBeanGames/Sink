namespace Sink.Models;

public enum ListenKind { Song, PodcastEpisode }

/// <summary>
/// One completed-or-substantial listen, logged for the Reflect page (task
/// 127). Recorded when playback of the item stops — naturally finishing or
/// switching away — as long as enough of it was actually heard (see the
/// recording call sites), not on every play attempt.
/// </summary>
public sealed class ListenEvent
{
    public ListenKind Kind { get; init; }
    public Guid ItemId { get; init; }
    public DateTime Occurred { get; init; } = DateTime.Now;
    public TimeSpan Duration { get; init; }

    /// <summary>Reached its natural end rather than being skipped/switched away from.</summary>
    public bool Completed { get; init; }

    // What was listened to, copied onto the event itself so Reflect can still
    // show it after the song (or podcast show) is deleted from the library.
    // Null on events recorded before this existed until they're backfilled
    // from a still-present library item.
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? Genre { get; set; }
    /// <summary>Podcast episodes only: the show's title.</summary>
    public string? Show { get; set; }

    public bool HasSnapshot => Title is not null;

    public void SnapshotFrom(Track track)
    {
        Title = track.Title;
        Artist = track.Artist;
        Album = track.Album;
        Genre = track.Genre;
    }
}
