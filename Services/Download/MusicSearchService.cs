using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sink.Services.Download;

public enum MusicSearchResultKind { Artist, Album, Track }

public sealed class MusicSearchResult : INotifyPropertyChanged
{
    private string _title = "";
    private string _artist = "";
    private string _album = "";
    private string _genre = "Unknown";
    private string _artwork = "";

    public required MusicSearchResultKind Kind { get; init; }
    public required string Title { get => _title; set { if (Set(ref _title, value)) OnPropertyChanged(nameof(Subtitle)); } }
    public string Artist { get => _artist; set { if (Set(ref _artist, value)) OnPropertyChanged(nameof(Subtitle)); } }
    public string Album { get => _album; set { if (Set(ref _album, value)) OnPropertyChanged(nameof(Subtitle)); } }
    public string Genre { get => _genre; set => Set(ref _genre, value); }
    public string Artwork { get => _artwork; set => Set(ref _artwork, value); }
    public string YouTubeUrl { get; internal set; } = "";
    public string DeezerUrl { get; internal set; } = "";
    public int TrackCount { get; internal set; }
    internal long DeezerId { get; init; }
    internal int MatchBonus { get; set; }

    /// <summary>
    /// Spotify's anonymous search endpoint is no longer available. Sink's
    /// existing Spotify workflow matches an individual song by artist/title
    /// through YouTube, so it is intentionally offered only for track rows.
    /// </summary>
    public bool HasSpotify => Kind == MusicSearchResultKind.Track
                              && !string.IsNullOrWhiteSpace(Title)
                              && !string.IsNullOrWhiteSpace(Artist);
    public bool HasYouTube => !string.IsNullOrWhiteSpace(YouTubeUrl);
    public bool HasDeezer => !string.IsNullOrWhiteSpace(DeezerUrl);
    public bool IsTitleReadOnly => Kind == MusicSearchResultKind.Track;
    public bool ShowArtistField => Kind != MusicSearchResultKind.Artist;
    public bool ShowAlbumField => Kind == MusicSearchResultKind.Track;
    public string KindLabel
    {
        get => Kind.ToString().ToUpperInvariant();
        set { }
    }
    public string TitleFieldLabel => Kind switch
    {
        MusicSearchResultKind.Artist => "Artist name",
        MusicSearchResultKind.Album => "Album title",
        _ => "Track title",
    };
    public string EditHint => IsTitleReadOnly
        ? "Track titles stay locked; the other populated metadata can be changed before queueing."
        : $"This {Kind.ToString().ToLowerInvariant()}'s populated metadata can be changed before queueing.";
    public string Subtitle => Kind switch
    {
        MusicSearchResultKind.Artist => "Full discography",
        MusicSearchResultKind.Album => string.Join("  ·  ", new[]
        {
            Artist,
            TrackCount > 0 ? $"{TrackCount} track{(TrackCount == 1 ? "" : "s")}" : "Album",
        }.Where(s => !string.IsNullOrWhiteSpace(s))),
        _ => string.Join("  ·  ", new[] { Artist, Album }.Where(s => !string.IsNullOrWhiteSpace(s))),
    };
    public string Providers => string.Join("  ", new[]
    {
        HasDeezer ? "DEEZER" : null,
        HasSpotify ? "SPOTIFY MATCH" : null,
        HasYouTube ? "YOUTUBE" : null,
    }.Where(s => s is not null));

    internal int ProviderCount => (HasDeezer ? 1 : 0) + (HasSpotify ? 1 : 0) + (HasYouTube ? 1 : 0);

    internal void MergeFrom(MusicSearchResult other)
    {
        if (string.IsNullOrWhiteSpace(Artist)) Artist = other.Artist;
        if (string.IsNullOrWhiteSpace(Album)) Album = other.Album;
        if ((Genre is "" or "Unknown") && other.Genre is not ("" or "Unknown")) Genre = other.Genre;
        if (string.IsNullOrWhiteSpace(Artwork)) Artwork = other.Artwork;
        if (string.IsNullOrWhiteSpace(YouTubeUrl)) YouTubeUrl = other.YouTubeUrl;
        if (string.IsNullOrWhiteSpace(DeezerUrl)) DeezerUrl = other.DeezerUrl;
        if (TrackCount == 0) TrackCount = other.TrackCount;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record MusicSearchResponse(
    IReadOnlyList<MusicSearchResult> Results,
    IReadOnlyList<string> ProviderErrors,
    string Summary);

/// <summary>
/// Keyless catalog search. Exact artists become an artist page followed by
/// their complete Deezer discography; exact albums become an album page
/// followed by its individual tracks; other searches return typed artist,
/// album, and track results instead of flattening everything into songs.
/// </summary>
public static class MusicSearchService
{
    private const string DeezerApi = "https://api.deezer.com";
    private static readonly HttpClient Http = CreateClient();
    private static readonly Regex Noise = new(
        @"\s*[\[(](?:official\s+(?:music\s+)?video|audio|lyrics?|visuali[sz]er)[^\])]*[\])]\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex KeyChars = new(@"[^a-z0-9]+", RegexOptions.Compiled);

    private sealed record ProviderResult<T>(T Value, string? Error = null);
    private sealed record YouTubeChannel(string Name, string Url);
    private sealed record YouTubeSearchData(
        IReadOnlyList<MusicSearchResult> Tracks,
        IReadOnlyList<YouTubeChannel> Channels);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Sink/1.0 (+https://github.com)");
        return client;
    }

    public static async Task<MusicSearchResponse> SearchAsync(
        string trackName, string albumName, string artistName, CancellationToken token = default)
    {
        var combinedQuery = string.Join(" ", new[] { trackName, albumName, artistName }.Where(s => !string.IsNullOrWhiteSpace(s)));
        
        var deezerQuery = combinedQuery;

        var ytParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(trackName)) ytParts.Add(trackName.Trim());
        if (!string.IsNullOrWhiteSpace(albumName)) ytParts.Add(albumName.Trim());
        if (!string.IsNullOrWhiteSpace(artistName)) ytParts.Add(artistName.Trim());
        var ytQuery = string.Join(" ", ytParts);

        if (string.IsNullOrWhiteSpace(deezerQuery)) deezerQuery = " ";
        if (string.IsNullOrWhiteSpace(ytQuery)) ytQuery = " ";

        var artistsTask = SafeProviderAsync(
            "Deezer artists", () => SearchDeezerArtistsAsync(deezerQuery, token),
            (IReadOnlyList<MusicSearchResult>)[]);
        var albumsTask = SafeProviderAsync(
            "Deezer albums", () => SearchDeezerAlbumsAsync(deezerQuery, albumName, token),
            (Albums: new List<MusicSearchResult>(), Artists: new List<MusicSearchResult>()));
        var tracksTask = SafeProviderAsync(
            "Deezer tracks", () => SearchDeezerTracksAsync(deezerQuery, trackName, token),
            (Tracks: new List<MusicSearchResult>(), Artists: new List<MusicSearchResult>(), Albums: new List<MusicSearchResult>()));
        var youtubeTask = SafeProviderAsync(
            "YouTube", () => SearchYouTubeAsync(ytQuery, token), new YouTubeSearchData([], []));

        await Task.WhenAll(artistsTask, albumsTask, tracksTask, youtubeTask).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        var artistsResult = await artistsTask.ConfigureAwait(false);
        var albumsResult = await albumsTask.ConfigureAwait(false);
        var tracksResult = await tracksTask.ConfigureAwait(false);
        var youtubeResult = await youtubeTask.ConfigureAwait(false);
        var artists = artistsResult.Value.ToList();
        var albums = albumsResult.Value.Albums;
        var tracks = tracksResult.Value.Tracks;
        var youtube = youtubeResult.Value;
        var errors = new List<string?>
        {
            artistsResult.Error, albumsResult.Error, tracksResult.Error, youtubeResult.Error,
        };

        foreach (var a in albumsResult.Value.Artists)
        {
            if (!artists.Any(x => x.DeezerId == a.DeezerId)) artists.Add(a);
        }
        foreach (var a in tracksResult.Value.Artists)
        {
            if (!artists.Any(x => x.DeezerId == a.DeezerId)) artists.Add(a);
        }
        foreach (var a in tracksResult.Value.Albums)
        {
            if (!albums.Any(x => x.DeezerId == a.DeezerId)) albums.Add(a);
        }

        foreach (var artist in artists) AttachMatchingChannel(artist, youtube.Channels);

        var generic = new List<MusicSearchResult>();
        generic.AddRange(artists);
        generic.AddRange(albums);
        generic.AddRange(MergeTracks(tracks, youtube.Tracks, includeUnmatchedYouTube: true));

        generic = generic
            .OrderByDescending(x => CalculateMatchScore(trackName, albumName, artistName, combinedQuery, x))
            .ThenBy(x => (int)x.Kind)
            .Take(150)
            .ToList();

        return Response(generic, errors,
            $"{generic.Count} catalog result{(generic.Count == 1 ? "" : "s")} across artists, albums, and tracks.");
    }

    public static string SpotifyMatchUrl(MusicSearchResult result) =>
        "ytsearch1:" + string.Join(' ', new[] { result.Artist, result.Title }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public static async Task<string?> CacheArtworkAsync(string value, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (File.Exists(value)) return value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")) return null;

        var directory = Path.Combine(LibraryStore.Directory, "search-artwork");
        Directory.CreateDirectory(directory);
        var name = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri))) + ".image";
        var path = Path.Combine(directory, name);
        if (File.Exists(path)) return path;
        try
        {
            var bytes = await Http.GetByteArrayAsync(uri, token).ConfigureAwait(false);
            await File.WriteAllBytesAsync(path, bytes, token).ConfigureAwait(false);
            return path;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException)
        {
            return null;
        }
    }

    private static MusicSearchResponse Response(
        IReadOnlyList<MusicSearchResult> results, IEnumerable<string?> errors, string summary) =>
        new(results, errors.Where(e => !string.IsNullOrWhiteSpace(e)).Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList(), summary);

    private static async Task<ProviderResult<T>> SafeProviderAsync<T>(
        string name, Func<Task<T>> search, T fallback)
    {
        try { return new ProviderResult<T>(await search().ConfigureAwait(false)); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"{name} music search failed: {ex.Message}");
            return new ProviderResult<T>(fallback, $"{name}: {ex.Message}");
        }
    }

    private static async Task<IReadOnlyList<MusicSearchResult>> SearchDeezerArtistsAsync(
        string query, CancellationToken token)
    {
        using var doc = await GetJsonAsync(
            $"{DeezerApi}/search/artist?limit=100&q={Uri.EscapeDataString(query)}", token).ConfigureAwait(false);
        return ReadData(doc.RootElement).Select(item =>
        {
            var id = Number(item, "id");
            var name = Text(item, "name", "Unknown Artist");
            return new MusicSearchResult
            {
                Kind = MusicSearchResultKind.Artist,
                DeezerId = id,
                Title = name,
                Artist = name,
                Artwork = Text(item, "picture_xl", Text(item, "picture_medium", "")),
                DeezerUrl = id > 0 ? $"https://www.deezer.com/artist/{id}" : "",
            };
        }).Where(r => r.DeezerId > 0).ToList();
    }

    private static async Task<(List<MusicSearchResult> Albums, List<MusicSearchResult> Artists)> SearchDeezerAlbumsAsync(
        string query, string targetQuery, CancellationToken token)
    {
        var albums = new List<MusicSearchResult>();
        var artists = new List<MusicSearchResult>();

        using var doc = await GetJsonAsync(
            $"{DeezerApi}/search/album?limit=100&q={Uri.EscapeDataString(query)}", token).ConfigureAwait(false);
        
        var matchTarget = string.IsNullOrWhiteSpace(targetQuery) ? query : targetQuery;
        var hasExplicitTarget = !string.IsNullOrWhiteSpace(targetQuery);

        foreach (var item in ReadData(doc.RootElement))
        {
            var alb = AlbumResult(item);
            if (alb.DeezerId > 0) albums.Add(alb);

            bool isExact = alb.Title.Equals(matchTarget, StringComparison.OrdinalIgnoreCase);

            if (item.TryGetProperty("artist", out var artToken))
            {
                var aId = Number(artToken, "id");
                if (aId > 0 && !artists.Any(a => a.DeezerId == aId))
                {
                    var name = Text(artToken, "name", "Unknown Artist");
                    artists.Add(new MusicSearchResult
                    {
                        Kind = MusicSearchResultKind.Artist,
                        DeezerId = aId,
                        Title = name,
                        Artist = name,
                        Artwork = Text(artToken, "picture_xl", Text(artToken, "picture_medium", "")),
                        DeezerUrl = $"https://www.deezer.com/artist/{aId}",
                        MatchBonus = (isExact && hasExplicitTarget) ? 1100 : 0
                    });
                }
            }
        }
        return (albums, artists);
    }

    private static async Task<(List<MusicSearchResult> Tracks, List<MusicSearchResult> Artists, List<MusicSearchResult> Albums)> SearchDeezerTracksAsync(
        string query, string targetQuery, CancellationToken token)
    {
        var tracks = new List<MusicSearchResult>();
        var artists = new List<MusicSearchResult>();
        var albums = new List<MusicSearchResult>();

        using var doc = await GetJsonAsync(
            $"{DeezerApi}/search/track?limit=100&q={Uri.EscapeDataString(query)}", token).ConfigureAwait(false);
        
        var matchTarget = string.IsNullOrWhiteSpace(targetQuery) ? query : targetQuery;
        var hasExplicitTarget = !string.IsNullOrWhiteSpace(targetQuery);

        foreach (var item in ReadData(doc.RootElement))
        {
            var trk = TrackResult(item, "", "", "");
            if (trk.DeezerId > 0) tracks.Add(trk);

            bool isExact = trk.Title.Equals(matchTarget, StringComparison.OrdinalIgnoreCase);

            if (item.TryGetProperty("artist", out var artToken))
            {
                var aId = Number(artToken, "id");
                if (aId > 0 && !artists.Any(a => a.DeezerId == aId))
                {
                    var name = Text(artToken, "name", "Unknown Artist");
                    artists.Add(new MusicSearchResult
                    {
                        Kind = MusicSearchResultKind.Artist,
                        DeezerId = aId,
                        Title = name,
                        Artist = name,
                        Artwork = Text(artToken, "picture_xl", Text(artToken, "picture_medium", "")),
                        DeezerUrl = $"https://www.deezer.com/artist/{aId}",
                        MatchBonus = (isExact && hasExplicitTarget) ? 1100 : 0
                    });
                }
            }
            if (item.TryGetProperty("album", out var albToken))
            {
                var aId = Number(albToken, "id");
                if (aId > 0 && !albums.Any(a => a.DeezerId == aId))
                {
                    albums.Add(new MusicSearchResult
                    {
                        Kind = MusicSearchResultKind.Album,
                        DeezerId = aId,
                        Title = Text(albToken, "title", "Unknown Album"),
                        Artist = trk.Artist,
                        Artwork = Text(albToken, "cover_xl", Text(albToken, "cover_medium", "")),
                        DeezerUrl = $"https://www.deezer.com/album/{aId}",
                        MatchBonus = (isExact && hasExplicitTarget) ? 1050 : 0
                    });
                }
            }
        }
        return (tracks, artists, albums);
    }

    private static async Task<IReadOnlyList<MusicSearchResult>> GetArtistAlbumsAsync(
        long artistId, string artist, CancellationToken token)
    {
        var albums = new List<MusicSearchResult>();
        string? next = $"{DeezerApi}/artist/{artistId}/albums?limit=100";
        var pages = 0;
        while (!string.IsNullOrWhiteSpace(next) && pages++ < 20)
        {
            using var doc = await GetJsonAsync(next, token).ConfigureAwait(false);
            var root = doc.RootElement;
            foreach (var item in ReadData(root))
            {
                var result = AlbumResult(item, artist);
                if (result.DeezerId > 0) albums.Add(result);
            }
            next = TextOrNull(root, "next");
        }
        return albums.DistinctBy(a => a.DeezerId).ToList();
    }

    private static async Task<IReadOnlyList<MusicSearchResult>> GetAlbumTracksAsync(
        MusicSearchResult album, CancellationToken token)
    {
        using var doc = await GetJsonAsync($"{DeezerApi}/album/{album.DeezerId}", token).ConfigureAwait(false);
        var root = doc.RootElement;
        var albumTitle = Text(root, "title", album.Title);
        var albumArtist = NestedText(root, "artist", "name", album.Artist);
        var artwork = Text(root, "cover_xl", Text(root, "cover_medium", album.Artwork));
        var genre = FirstGenre(root);
        if (!root.TryGetProperty("tracks", out var trackPage)) return [];

        var tracks = new List<MusicSearchResult>();
        AddTracks(trackPage, tracks, albumTitle, albumArtist, artwork, genre);
        var next = TextOrNull(trackPage, "next");
        var pages = 0;
        while (!string.IsNullOrWhiteSpace(next) && pages++ < 20)
        {
            using var pageDoc = await GetJsonAsync(next, token).ConfigureAwait(false);
            var page = pageDoc.RootElement;
            AddTracks(page, tracks, albumTitle, albumArtist, artwork, genre);
            next = TextOrNull(page, "next");
        }
        return tracks.DistinctBy(t => t.DeezerId).ToList();
    }

    private static void AddTracks(
        JsonElement page, ICollection<MusicSearchResult> tracks,
        string album, string artist, string artwork, string genre)
    {
        foreach (var item in ReadData(page))
        {
            var result = TrackResult(item, album, artwork, genre, artist);
            if (result.DeezerId > 0) tracks.Add(result);
        }
    }

    private static MusicSearchResult AlbumResult(JsonElement item, string fallbackArtist = "")
    {
        var id = Number(item, "id");
        return new MusicSearchResult
        {
            Kind = MusicSearchResultKind.Album,
            DeezerId = id,
            Title = Text(item, "title", "Unknown Album"),
            Artist = NestedText(item, "artist", "name", fallbackArtist),
            Artwork = Text(item, "cover_xl", Text(item, "cover_medium", "")),
            TrackCount = (int)Number(item, "nb_tracks"),
            DeezerUrl = id > 0 ? $"https://www.deezer.com/album/{id}" : "",
        };
    }

    private static MusicSearchResult TrackResult(
        JsonElement item, string fallbackAlbum, string fallbackArtwork, string fallbackGenre,
        string fallbackArtist = "")
    {
        var id = Number(item, "id");
        return new MusicSearchResult
        {
            Kind = MusicSearchResultKind.Track,
            DeezerId = id,
            Title = Text(item, "title", "Untitled"),
            Artist = NestedText(item, "artist", "name", fallbackArtist),
            Album = NestedText(item, "album", "title", fallbackAlbum),
            Genre = string.IsNullOrWhiteSpace(fallbackGenre) ? "Unknown" : fallbackGenre,
            Artwork = NestedText(item, "album", "cover_xl",
                NestedText(item, "album", "cover_medium", fallbackArtwork)),
            DeezerUrl = id > 0 ? $"https://www.deezer.com/track/{id}" : "",
        };
    }

    private static async Task<YouTubeSearchData> SearchYouTubeAsync(
        string query, CancellationToken token)
    {
        if (!File.Exists(ToolManager.YtDlpPath))
            throw new InvalidOperationException("yt-dlp is still being installed");
        var psi = new ProcessStartInfo(ToolManager.YtDlpPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "-J", "--flat-playlist", "--no-warnings", "ytsearch18:" + query })
            psi.ArgumentList.Add(argument);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start YouTube search");
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(FirstLine(error) ?? "YouTube search failed");

        using var doc = JsonDocument.Parse(output);
        if (!doc.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            return new YouTubeSearchData([], []);
        var tracks = new List<MusicSearchResult>();
        var channels = new List<YouTubeChannel>();
        foreach (var item in entries.EnumerateArray())
        {
            var id = Text(item, "id", "");
            if (id.Length == 0) continue;
            var channel = CleanChannel(Text(item, "channel", Text(item, "uploader", "Unknown Artist")));
            var channelUrl = Text(item, "channel_url", "");
            if (channelUrl.Length == 0 && TextOrNull(item, "channel_id") is { } channelId)
                channelUrl = $"https://www.youtube.com/channel/{channelId}";
            if (channelUrl.Length > 0) channels.Add(new YouTubeChannel(channel, channelUrl));

            var (artist, title) = ParseYouTubeTitle(Text(item, "title", "Untitled"), channel);
            var url = Text(item, "url", "");
            if (!Uri.TryCreate(url, UriKind.Absolute, out _)) url = $"https://www.youtube.com/watch?v={id}";
            tracks.Add(new MusicSearchResult
            {
                Kind = MusicSearchResultKind.Track,
                Title = title,
                Artist = artist,
                Genre = "Unknown",
                Artwork = Text(item, "thumbnail", ""),
                YouTubeUrl = url,
            });
        }
        return new YouTubeSearchData(
            tracks,
            channels.DistinctBy(c => c.Url, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static IReadOnlyList<MusicSearchResult> MergeTracks(
        IEnumerable<MusicSearchResult> deezer,
        IEnumerable<MusicSearchResult> youtube,
        bool includeUnmatchedYouTube)
    {
        var merged = new Dictionary<string, MusicSearchResult>(StringComparer.Ordinal);
        foreach (var result in deezer) merged.TryAdd(ResultKey(result), result);
        foreach (var result in youtube)
        {
            var key = ResultKey(result);
            if (merged.TryGetValue(key, out var existing)) existing.MergeFrom(result);
            else if (includeUnmatchedYouTube) merged[key] = result;
        }
        return merged.Values
            .OrderByDescending(r => r.ProviderCount)
            .ThenByDescending(r => r.HasDeezer)
            .ThenBy(r => r.Artist, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AttachMatchingChannel(
        MusicSearchResult artist, IReadOnlyList<YouTubeChannel> channels)
    {
        var key = Normalize(artist.Title);
        var match = channels.FirstOrDefault(c => Normalize(c.Name) == key)
                    ?? channels.FirstOrDefault(c =>
                        Normalize(c.Name).Contains(key, StringComparison.Ordinal)
                        || key.Contains(Normalize(c.Name), StringComparison.Ordinal));
        if (match is not null) artist.YouTubeUrl = match.Url;
    }

    private static (string artist, string title) ParseYouTubeTitle(string raw, string channel)
    {
        var title = Noise.Replace(raw, "").Trim();
        foreach (var separator in new[] { " - ", " – ", " — " })
        {
            var at = title.IndexOf(separator, StringComparison.Ordinal);
            if (at <= 0 || at + separator.Length >= title.Length) continue;
            var left = title[..at].Trim();
            var right = title[(at + separator.Length)..].Trim();
            var channelKey = Normalize(channel);
            if (Normalize(left) == channelKey) return (left, right);
            if (Normalize(right) == channelKey) return (right, left);
        }
        return (channel, title);
    }

    private static int CalculateMatchScore(string trackQuery, string albumQuery, string artistQuery, string combinedQuery, MusicSearchResult result)
    {
        var title = result.Title.Trim();
        var artist = result.Artist.Trim();
        var combined = $"{result.Artist} {result.Title}".Trim();
        var q = combinedQuery.Trim();

        var log = new List<string>();

        int EvaluateField(string fieldValue, string targetQuery, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(targetQuery) || string.IsNullOrWhiteSpace(fieldValue)) return 0;
            var t = fieldValue.Trim();
            var qTarget = targetQuery.Trim();
            if (t.Equals(qTarget, StringComparison.OrdinalIgnoreCase)) { log.Add($"{fieldName} Exact (+1000)"); return 1000; }
            if (t.StartsWith(qTarget, StringComparison.OrdinalIgnoreCase)) { log.Add($"{fieldName} StartsWith (+900)"); return 900; }
            if (t.EndsWith(qTarget, StringComparison.OrdinalIgnoreCase)) { log.Add($"{fieldName} EndsWith (+800)"); return 800; }
            if (t.Contains(qTarget, StringComparison.OrdinalIgnoreCase)) { log.Add($"{fieldName} Contains (+700)"); return 700; }
            return 0;
        }

        int baseScore = 0;
        bool scoredFields = false;

        if (result.Kind == MusicSearchResultKind.Track && !string.IsNullOrWhiteSpace(trackQuery))
        {
            baseScore += EvaluateField(title, trackQuery, "TrackTitle");
            scoredFields = true;
        }
        else if (result.Kind == MusicSearchResultKind.Album && !string.IsNullOrWhiteSpace(albumQuery))
        {
            baseScore += EvaluateField(title, albumQuery, "AlbumTitle");
            scoredFields = true;
        }

        if (!string.IsNullOrWhiteSpace(artistQuery))
        {
            int artistScore = EvaluateField(artist, artistQuery, "ArtistName");
            if (result.Kind == MusicSearchResultKind.Artist)
            {
                var titleArtistScore = EvaluateField(title, artistQuery, "ArtistTitleFallback");
                if (titleArtistScore > artistScore)
                {
                    log.RemoveAll(x => x.StartsWith("ArtistName"));
                    artistScore = titleArtistScore;
                }
                else
                {
                    log.RemoveAll(x => x.StartsWith("ArtistTitleFallback"));
                }
            }
            baseScore += artistScore;
            scoredFields = true;
        }

        if (!scoredFields)
        {
            bool Matches(Func<string, string, bool> condition) =>
                condition(title, q) || condition(combined, q);

            if (Matches((t, s) => t.Equals(s, StringComparison.OrdinalIgnoreCase))) { log.Add("Combined Exact (+1000)"); baseScore = 1000; }
            else if (Matches((t, s) => t.StartsWith(s, StringComparison.OrdinalIgnoreCase))) { log.Add("Combined StartsWith (+900)"); baseScore = 900; }
            else if (Matches((t, s) => t.EndsWith(s, StringComparison.OrdinalIgnoreCase))) { log.Add("Combined EndsWith (+800)"); baseScore = 800; }
            else if (Matches((t, s) => t.Contains(s, StringComparison.OrdinalIgnoreCase))) { log.Add("Combined Contains (+700)"); baseScore = 700; }
        }

        var words = KeyChars.Replace(q.ToLowerInvariant(), " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        
        int wordBonus = 0;
        if (words.Length > 0)
        {
            var searchableTitle = title.ToLowerInvariant();
            var searchableArtist = artist.ToLowerInvariant();
            foreach (var word in words)
            {
                if (searchableTitle.Contains(word) || searchableArtist.Contains(word)) wordBonus++;
            }
            if (wordBonus > 0) log.Add($"Words x{wordBonus} (+{wordBonus})");
        }

        if (result.MatchBonus > baseScore)
        {
            log.Add($"MatchBonus Override (+{result.MatchBonus} replaces {baseScore})");
            baseScore = result.MatchBonus;
        }

        var total = baseScore + wordBonus;
        if (total > 0 || result.MatchBonus > 0)
        {
            Log.Info($"[Score] {result.Kind,-6} | {result.Artist,-15} | {title,-20} => {total} [{string.Join(", ", log)}]");
        }

        return total;
    }

    private static bool Equivalent(string left, string right) => Normalize(left) == Normalize(right);

    private static string ResultKey(MusicSearchResult result) =>
        result.Kind + "|" + Normalize(result.Artist) + "|" + Normalize(result.Title);

    private static string Normalize(string value) =>
        KeyChars.Replace(CleanChannel(value).ToLowerInvariant(), "");

    private static string CleanChannel(string value) =>
        value.Replace(" - Topic", "", StringComparison.OrdinalIgnoreCase).Trim();

    private static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken token)
    {
        using var response = await Http.GetAsync(url, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        ThrowIfApiError(doc.RootElement);
        return doc;
    }

    private static IEnumerable<JsonElement> ReadData(JsonElement root) =>
        root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray()
            : [];

    private static void ThrowIfApiError(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error))
            throw new InvalidOperationException(Text(error, "message", "Deezer search failed"));
    }

    private static string FirstGenre(JsonElement root)
    {
        if (root.TryGetProperty("genres", out var genres))
            foreach (var item in ReadData(genres))
                if (TextOrNull(item, "name") is { } name) return name;
        return "Unknown";
    }

    private static string Text(JsonElement root, string property, string fallback) =>
        TextOrNull(root, property) ?? fallback;

    private static string? TextOrNull(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() is { Length: > 0 } text ? text : null
            : null;

    private static string NestedText(
        JsonElement root, string objectName, string propertyName, string fallback) =>
        root.TryGetProperty(objectName, out var nested) && nested.ValueKind == JsonValueKind.Object
            ? Text(nested, propertyName, fallback)
            : fallback;

    private static long Number(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.TryGetInt64(out var number) ? number : 0;

    private static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
}
