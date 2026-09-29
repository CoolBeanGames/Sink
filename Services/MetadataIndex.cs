using Sink.Models;

namespace Sink.Services;

/// <summary>
/// A live list of every title / artist / album / genre in the library, so any
/// text box that edits one of those can offer autocomplete. <see cref="Rebuild"/>
/// is called whenever the library changes. <see cref="ExtraSource"/> adds values
/// that aren't in the library yet (the download queue), read fresh on every
/// lookup since they're edited constantly.
/// </summary>
public static class MetadataIndex
{
    public static IReadOnlyList<string> Titles { get; private set; } = [];
    public static IReadOnlyList<string> Artists { get; private set; } = [];
    public static IReadOnlyList<string> Albums { get; private set; } = [];
    public static IReadOnlyList<string> Genres { get; private set; } = [];

    /// <summary>Non-library metadata to suggest alongside the library's own (the download queue).</summary>
    public static Func<IEnumerable<MetadataValues>>? ExtraSource { get; set; }

    /// <summary>Raised on the thread that called <see cref="Rebuild"/> after the lists change.</summary>
    public static event Action? Changed;

    public static void Rebuild(IEnumerable<Track> tracks)
    {
        var list = tracks as IReadOnlyCollection<Track> ?? tracks.ToList();
        Titles = Distinct(list.Select(t => t.Title));
        Artists = Distinct(list.Select(t => t.Artist));
        Albums = Distinct(list.Select(t => t.Album));
        Genres = Distinct(list.Select(t => t.Genre));
        Changed?.Invoke();
    }

    /// <summary>Extra-source values first (what's being worked on right now), then the library's.</summary>
    public static IReadOnlyList<string> TitleSuggestions() => Merge(v => v.Title, Titles);
    public static IReadOnlyList<string> ArtistSuggestions() => Merge(v => v.Artist, Artists);
    public static IReadOnlyList<string> AlbumSuggestions() => Merge(v => v.Album, Albums);
    public static IReadOnlyList<string> GenreSuggestions() => Merge(v => v.Genre, Genres);

    private static IReadOnlyList<string> Merge(Func<MetadataValues, string?> pick, IReadOnlyList<string> library)
    {
        IEnumerable<MetadataValues> extra;
        try { extra = ExtraSource?.Invoke().ToList() ?? []; }
        catch (InvalidOperationException) { extra = []; } // collection changed mid-walk; just skip this lookup's extras
        var fromExtra = Distinct(extra.Select(pick));
        return fromExtra.Count == 0 ? library : fromExtra.Concat(library).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<string> Distinct(IEnumerable<string?> values) => values
        .Where(v => !string.IsNullOrWhiteSpace(v))
        .Select(v => v!.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

/// <summary>One item's worth of suggestible metadata; any field may be null.</summary>
public readonly record struct MetadataValues(string? Title, string? Artist, string? Album, string? Genre);
