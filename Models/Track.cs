namespace Sink.Models;

public sealed class Track
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public required string Album { get; init; }
    public required string Genre { get; init; }
    public required string FileName { get; init; }
    public string? FilePath { get; init; }
    public int TrackNumber { get; init; }
    public int Year { get; init; }
    public TimeSpan Duration { get; init; }
    public string DurationText => Duration.ToString(@"m\:ss");
    public bool ExcludedFromShuffle { get; set; }
}
