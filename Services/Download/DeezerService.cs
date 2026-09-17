using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sink.Services.Download;

/// <summary>
/// Reads public Deezer metadata for links that Streamrip will download. Keeping
/// discovery here (instead of asking Streamrip to download just to learn the
/// queue shape) lets albums and playlists participate in Sink's normal
/// per-track selection, duplicate checking, metadata editing, and persistence.
/// </summary>
public static class DeezerService
{
    private const string ApiBase = "https://api.deezer.com";
    private static readonly HttpClient Http = CreateClient();

    private sealed record Target(string Type, long Id, string Url);
    private sealed record DeezerTrack(long Id, string Title, string Artist, string Album);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(45),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Sink/1.0 (+https://github.com)");
        return client;
    }

    public static bool IsDeezerLink(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return false;
        var host = uri.Host;
        return host.Equals("deezer.com", StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(".deezer.com", StringComparison.OrdinalIgnoreCase)
               || host.Equals("deezer.page.link", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<ScannedInfo> ScanAsync(string url, CancellationToken token = default)
    {
        var target = await ResolveTargetAsync(url, token).ConfigureAwait(false);
        return target.Type switch
        {
            "track" => await ScanTrackAsync(target, token).ConfigureAwait(false),
            "album" => await ScanAlbumAsync(target, token).ConfigureAwait(false),
            "playlist" => await ScanPlaylistAsync(target, token).ConfigureAwait(false),
            "artist" => await ScanArtistAsync(target, token).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported Deezer link type: {target.Type}"),
        };
    }

    private static async Task<ScannedInfo> ScanTrackAsync(Target target, CancellationToken token)
    {
        using var doc = await GetJsonAsync($"{ApiBase}/track/{target.Id}", token).ConfigureAwait(false);
        var root = doc.RootElement;
        ThrowIfApiError(root);
        var title = String(root, "title", "Unknown title");
        var artist = NestedString(root, "artist", "name", "Unknown Artist");
        var album = NestedString(root, "album", "title", title);
        return new ScannedInfo(title, artist, album, "Unknown", IsPlaylist: false, TrackCount: 1);
    }

    private static async Task<ScannedInfo> ScanAlbumAsync(Target target, CancellationToken token)
    {
        using var doc = await GetJsonAsync($"{ApiBase}/album/{target.Id}", token).ConfigureAwait(false);
        var root = doc.RootElement;
        ThrowIfApiError(root);
        var album = String(root, "title", "Unknown Album");
        var artist = NestedString(root, "artist", "name", "Unknown Artist");
        var genre = FirstGenre(root);
        var tracks = root.TryGetProperty("tracks", out var page)
            ? await ReadTrackPagesAsync(page, token).ConfigureAwait(false)
            : [];
        if (tracks.Count == 0)
            throw new InvalidOperationException("Deezer returned an album with no tracks");

        return TrackCollection(album, artist, genre, tracks, isMixedPlaylist: false);
    }

    private static async Task<ScannedInfo> ScanPlaylistAsync(Target target, CancellationToken token)
    {
        using var doc = await GetJsonAsync($"{ApiBase}/playlist/{target.Id}", token).ConfigureAwait(false);
        var root = doc.RootElement;
        ThrowIfApiError(root);
        var title = String(root, "title", "Deezer Playlist");
        var owner = NestedString(root, "creator", "name", "Various Artists");
        var tracks = root.TryGetProperty("tracks", out var page)
            ? await ReadTrackPagesAsync(page, token).ConfigureAwait(false)
            : [];
        if (tracks.Count == 0)
            throw new InvalidOperationException("Deezer returned a playlist with no tracks");

        return TrackCollection(title, owner, "Unknown", tracks, isMixedPlaylist: true);
    }

    private static async Task<ScannedInfo> ScanArtistAsync(Target target, CancellationToken token)
    {
        using var artistDoc = await GetJsonAsync($"{ApiBase}/artist/{target.Id}", token).ConfigureAwait(false);
        var artistRoot = artistDoc.RootElement;
        ThrowIfApiError(artistRoot);
        var artist = String(artistRoot, "name", "Unknown Artist");
        var albums = new List<ScannedAlbum>();
        string? next = $"{ApiBase}/artist/{target.Id}/albums?limit=100";
        var pages = 0;
        while (!string.IsNullOrWhiteSpace(next) && pages++ < 50)
        {
            using var pageDoc = await GetJsonAsync(next, token).ConfigureAwait(false);
            var root = pageDoc.RootElement;
            ThrowIfApiError(root);
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    var id = Number(item, "id");
                    if (id <= 0) continue;
                    albums.Add(new ScannedAlbum(
                        $"https://www.deezer.com/album/{id}",
                        String(item, "title", "Album")));
                }
            }
            next = StringOrNull(root, "next");
        }

        if (albums.Count == 0)
            throw new InvalidOperationException("Deezer returned no albums for that artist");
        var distinct = albums.DistinctBy(a => a.Url, StringComparer.OrdinalIgnoreCase).ToList();
        return new ScannedInfo(artist, artist, "", "Unknown", IsPlaylist: false,
            TrackCount: 0, Albums: distinct);
    }

    private static ScannedInfo TrackCollection(
        string name, string owner, string genre, IReadOnlyList<DeezerTrack> tracks, bool isMixedPlaylist) =>
        new(name, owner, name, genre, IsPlaylist: true, TrackCount: tracks.Count,
            TrackTitles: tracks.Select(t => t.Title).ToList(),
            TrackArtists: tracks.Select(t => t.Artist).ToList(),
            TrackUrls: tracks.Select(t => $"https://www.deezer.com/track/{t.Id}").ToList(),
            TrackAlbums: tracks.Select(t => t.Album).ToList(),
            IsMixedPlaylist: isMixedPlaylist);

    private static async Task<IReadOnlyList<DeezerTrack>> ReadTrackPagesAsync(
        JsonElement firstPage, CancellationToken token)
    {
        var tracks = new List<DeezerTrack>();
        AddTracks(firstPage, tracks);
        var next = StringOrNull(firstPage, "next");
        var pages = 0;
        while (!string.IsNullOrWhiteSpace(next) && pages++ < 50)
        {
            using var doc = await GetJsonAsync(next, token).ConfigureAwait(false);
            var root = doc.RootElement;
            ThrowIfApiError(root);
            AddTracks(root, tracks);
            next = StringOrNull(root, "next");
        }
        return tracks.DistinctBy(t => t.Id).ToList();
    }

    private static void AddTracks(JsonElement page, ICollection<DeezerTrack> tracks)
    {
        if (!page.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return;
        foreach (var item in data.EnumerateArray())
        {
            var id = Number(item, "id");
            if (id <= 0) continue;
            tracks.Add(new DeezerTrack(
                id,
                String(item, "title", "Untitled"),
                NestedString(item, "artist", "name", "Unknown Artist"),
                NestedString(item, "album", "title", "")));
        }
    }

    private static async Task<Target> ResolveTargetAsync(string url, CancellationToken token)
    {
        if (TryParseTarget(url, out var direct)) return direct;

        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var resolved = response.RequestMessage?.RequestUri?.AbsoluteUri;
        if (resolved is not null && TryParseTarget(resolved, out var redirected)) return redirected;

        var html = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        foreach (Match match in Regex.Matches(WebUtility.HtmlDecode(html),
                     @"https?://(?:www\.)?deezer\.com/(?:[a-z]{2}/)?(?:[a-z]{2}-[A-Z]{2}/)?(track|album|playlist|artist)/(\d+)",
                     RegexOptions.IgnoreCase))
        {
            if (long.TryParse(match.Groups[2].Value, out var id))
                return new Target(match.Groups[1].Value.ToLowerInvariant(), id, match.Value);
        }

        throw new InvalidOperationException("Sink couldn't identify the track, album, playlist, or artist in that Deezer link");
    }

    private static bool TryParseTarget(string value, out Target target)
    {
        target = null!;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return false;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 1 < segments.Length; i++)
        {
            var type = segments[i].ToLowerInvariant();
            if (type is not ("track" or "album" or "playlist" or "artist")) continue;
            if (!long.TryParse(segments[i + 1], out var id) || id <= 0) continue;
            target = new Target(type, id, $"https://www.deezer.com/{type}/{id}");
            return true;
        }
        return false;
    }

    private static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken token)
    {
        using var response = await Http.GetAsync(url, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
    }

    private static void ThrowIfApiError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error)) return;
        var message = String(error, "message", "Deezer could not read that link");
        throw new InvalidOperationException(message);
    }

    private static string FirstGenre(JsonElement root)
    {
        if (root.TryGetProperty("genres", out var genres)
            && genres.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
                if (StringOrNull(item, "name") is { } name) return name;
        }
        return "Unknown";
    }

    private static string NestedString(
        JsonElement root, string objectName, string propertyName, string fallback) =>
        root.TryGetProperty(objectName, out var nested) && nested.ValueKind == JsonValueKind.Object
            ? String(nested, propertyName, fallback)
            : fallback;

    private static string String(JsonElement root, string name, string fallback) =>
        StringOrNull(root, name) ?? fallback;

    private static string? StringOrNull(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() is { Length: > 0 } text ? text : null
            : null;

    private static long Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;
}
