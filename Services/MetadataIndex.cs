using Sink.Models;

namespace Sink.Services;

/// <summary>
/// A live list of every artist / album / genre in the library, so any text box
/// that edits one of those can offer autocomplete. <see cref="Rebuild"/> is
/// called whenever the library changes.
/// </summary>
public static class MetadataIndex
{
    public static IReadOnlyList<string> Artists { get; private set; } = [];
    public static IReadOnlyList<string> Albums { get; private set; } = [];
    public static IReadOnlyList<string> Genres { get; private set; } = [];

    /// <summary>Raised on the thread that called <see cref="Rebuild"/> after the lists change.</summary>
    public static event Action? Changed;

    public static void Rebuild(IEnumerable<Track> tracks)
    {
        var list = tracks as IReadOnlyCollection<Track> ?? tracks.ToList();
        Artists = Distinct(list.Select(t => t.Artist));
        Albums = Distinct(list.Select(t => t.Album));
        Genres = Distinct(list.Select(t => t.Genre));
        Changed?.Invoke();
    }

    private static IReadOnlyList<string> Distinct(IEnumerable<string?> values) => values
        .Where(v => !string.IsNullOrWhiteSpace(v))
        .Select(v => v!.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
        .ToList();
}
