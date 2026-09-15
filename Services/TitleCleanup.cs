using System.Text.RegularExpressions;

namespace Sink.Services;

/// <summary>
/// Strips common download-artifact noise out of a track title: bracketed/
/// parenthesised/dash-suffixed "Official (Music) Video" tags, "- Cover",
/// "feat. X"/"feat - X" credits, a literal "Album -" prefix, and the
/// track's own Artist/Album name repeated as a "Name -" prefix — down to
/// just the song name.
/// </summary>
public static partial class TitleCleanup
{
    [GeneratedRegex(
        @"\s*\[\s*OFFICIAL\s+(MUSIC\s+)?VIDEO\s*\]\s*" +
        @"|\s*\(\s*OFFICIAL\s+(MUSIC\s+)?VIDEO\s*\)\s*" +
        @"|\s*-\s*OFFICIAL\s+(MUSIC\s+)?VIDEO\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex OfficialTagRegex();

    [GeneratedRegex(@"\s*-\s*Cover\b", RegexOptions.IgnoreCase)]
    private static partial Regex CoverTagRegex();

    [GeneratedRegex(@"\s*[-(]?\s*\bfeat(?:\.|\b)\s*-?\s*.*$", RegexOptions.IgnoreCase)]
    private static partial Regex FeatTagRegex();

    [GeneratedRegex(@"\bAlbum\s*-\s*", RegexOptions.IgnoreCase)]
    private static partial Regex AlbumPrefixRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex CollapseSpacesRegex();

    [GeneratedRegex(@"^[\s-]+|[\s-]+$")]
    private static partial Regex TrimDashesRegex();

    /// <summary>Returns the cleaned title. Returns the original string unchanged if there was nothing to strip.</summary>
    public static string Clean(string title, string artist, string album)
    {
        var t = title;
        t = OfficialTagRegex().Replace(t, " ");
        t = CoverTagRegex().Replace(t, "");
        t = FeatTagRegex().Replace(t, "");
        t = AlbumPrefixRegex().Replace(t, "");
        if (!string.IsNullOrWhiteSpace(artist)) t = t.Replace($"{artist} -", "", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(album)) t = t.Replace($"{album} -", "", StringComparison.OrdinalIgnoreCase);
        t = CollapseSpacesRegex().Replace(t, " ");
        t = TrimDashesRegex().Replace(t, "");
        return t.Trim();
    }
}
