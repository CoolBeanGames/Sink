using System.Text.Json.Serialization;

namespace Sink.Models;

public sealed class Track
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Title { get; set; }
    public required string Artist { get; set; }
    public required string Album { get; set; }
    public required string Genre { get; set; }
    public required string FileName { get; init; }
    public string? FilePath { get; init; }
    public int TrackNumber { get; set; }
    public int Year { get; set; }
    public TimeSpan Duration { get; init; }
    public bool ExcludedFromShuffle { get; set; }

    /// <summary>Absolute path to extracted cover art (jpg/png) under %AppData%/Sink/artwork, if any.</summary>
    public string? ArtworkPath { get; set; }

    [JsonIgnore]
    public string DurationText => Duration.ToString(@"m\:ss");
}
