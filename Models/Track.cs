using System.Text.Json.Serialization;

namespace Sink.Models;

public sealed class Track
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Title { get; set; }
    public required string Artist { get; set; }
    public required string Album { get; set; }
    public required string Genre { get; set; }
    public required string FileName { get; set; }
    public string? FilePath { get; set; }
    public int TrackNumber { get; set; }
    public int Year { get; set; }
    public TimeSpan Duration { get; init; }
    public bool ExcludedFromShuffle { get; set; }

    /// <summary>Heart-icon favorite (task 127 — Reflect).</summary>
    public bool IsFavorite { get; set; }

    /// <summary>
    /// This track's PlayCount on each iPod (keyed by device serial) as of the
    /// last sync — the baseline the next sync diffs against so device plays
    /// aren't double-counted into Reflect (task 128).
    /// </summary>
    public Dictionary<string, int>? SyncedIpodPlayCounts { get; set; }

    /// <summary>Absolute path to extracted cover art (jpg/png) under %AppData%/Sink/artwork, if any.</summary>
    public string? ArtworkPath { get; set; }

    [JsonIgnore]
    public string DurationText => Duration.ToString(@"m\:ss");
}
