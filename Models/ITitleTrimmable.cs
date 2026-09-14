namespace Sink.Models;

/// <summary>
/// Minimal shape the title-trimming tool needs — implemented by both
/// <see cref="Track"/> (the Tags page) and DownloadNode (the download page),
/// so the same dialog works from either place (task 159).
/// </summary>
public interface ITitleTrimmable
{
    string Title { get; set; }
    int TrackNumber { get; set; }
}
