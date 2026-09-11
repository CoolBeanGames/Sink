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
}
