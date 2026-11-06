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
    private static readonly Regex KeyChars = new(@"[^\p{L}\p{Nd}]+", RegexOptions.Compiled);
    private static readonly Regex TrackSubtitle = new(@"\s+[\[(][^\])]+[\])]\s*$", RegexOptions.Compiled);

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
        trackName = trackName.Trim();
        albumName = albumName.Trim();
        artistName = artistName.Trim();
        var combinedQuery = JoinTerms(trackName, albumName, artistName);
        if (combinedQuery.Length == 0) return Response([], [], "Enter a song, album, or artist to search.");

        Task<ProviderResult<T>> Find<T>(string name, Func<Task<T>> search, T fallback, bool enabled = true) =>
            enabled ? SafeProviderAsync(name, search, fallback, token) : Task.FromResult(new ProviderResult<T>(fallback));
        IReadOnlyList<MusicSearchResult> empty = [];
        var emptyAlbums = (Albums: new List<MusicSearchResult>(), Artists: new List<MusicSearchResult>());
        var emptyTracks = (Tracks: new List<MusicSearchResult>(), Artists: new List<MusicSearchResult>(), Albums: new List<MusicSearchResult>());
        var emptyYouTube = new YouTubeSearchData([], []);
        var titleSearch = trackName.Length > 0 && combinedQuery != trackName;
        var artistQuery = artistName.Length > 0 ? artistName : combinedQuery;
        var albumQuery = albumName.Length > 0 ? JoinTerms(albumName, artistName) : combinedQuery;

        var artistsTask = Find("Deezer artists", () => SearchDeezerArtistsAsync(artistQuery, token), empty);
        var albumsTask = Find("Deezer albums", () => SearchDeezerAlbumsAsync(albumQuery, token), emptyAlbums);
        var tracksTask = Find("Deezer tracks", () => SearchDeezerTracksAsync(combinedQuery, token), emptyTracks);
        var sameTitleTask = Find("Deezer song titles", () => SearchDeezerTracksAsync(trackName, token), emptyTracks, titleSearch);
        var youtubeTask = Find("YouTube", () => SearchYouTubeAsync(combinedQuery, trackName, token), emptyYouTube);
        var youtubeTitleTask = Find("YouTube", () => SearchYouTubeAsync(trackName, trackName, token), emptyYouTube, titleSearch);

        await Task.WhenAll(artistsTask, albumsTask, tracksTask, sameTitleTask, youtubeTask, youtubeTitleTask).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        var artistsResult = await artistsTask.ConfigureAwait(false);
        var albumsResult = await albumsTask.ConfigureAwait(false);
        var tracksResult = await tracksTask.ConfigureAwait(false);
        var sameTitleResult = await sameTitleTask.ConfigureAwait(false);
        var youtubeResult = await youtubeTask.ConfigureAwait(false);
        var youtubeTitleResult = await youtubeTitleTask.ConfigureAwait(false);
        var artists = artistsResult.Value.Concat(albumsResult.Value.Artists)
            .Concat(tracksResult.Value.Artists).Concat(sameTitleResult.Value.Artists).DistinctBy(a => a.DeezerId).ToList();
        var albums = albumsResult.Value.Albums.Concat(tracksResult.Value.Albums)
            .Concat(sameTitleResult.Value.Albums).DistinctBy(a => a.DeezerId).ToList();

        var exactArtist = artists.FirstOrDefault(a => Equivalent(artistName, a.Title));
        var discographyTask = Find("Deezer discography", () => GetArtistAlbumsAsync(exactArtist!.DeezerId, exactArtist.Title, token),
            empty, exactArtist is not null);
        bool IsTargetAlbum(MusicSearchResult a) => Equivalent(albumName, a.Title)
            && (artistName.Length == 0 || Equivalent(artistName, a.Artist));
        var exactAlbum = albums.FirstOrDefault(IsTargetAlbum);
        if (exactAlbum is null && albumName.Length > 0)
            exactAlbum = (await discographyTask.ConfigureAwait(false)).Value.FirstOrDefault(IsTargetAlbum);
        var albumTracksTask = Find("Deezer album tracks", () => GetAlbumTracksAsync(exactAlbum!, token), empty, exactAlbum is not null);
        var discography = await discographyTask.ConfigureAwait(false);
        var albumTracks = await albumTracksTask.ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        var channels = youtubeResult.Value.Channels.Concat(youtubeTitleResult.Value.Channels).ToList();
        foreach (var artist in artists) AttachMatchingChannel(artist, channels);

        var songs = MergeTracks(albumTracks.Value.Concat(tracksResult.Value.Tracks).Concat(sameTitleResult.Value.Tracks),
            youtubeResult.Value.Tracks.Concat(youtubeTitleResult.Value.Tracks));
        var ranked = RankResults(artists.Concat(albums.Concat(discography.Value).DistinctBy(a => a.DeezerId)).Concat(songs),
            trackName, albumName, artistName).Take(150).ToList();
        return Response(ranked,
            [artistsResult.Error, albumsResult.Error, tracksResult.Error, sameTitleResult.Error,
             youtubeResult.Error, youtubeTitleResult.Error, discography.Error, albumTracks.Error],
            $"{ranked.Count} catalog result{(ranked.Count == 1 ? "" : "s")} across artists, albums, and tracks.");
    }

    private static string JoinTerms(params string[] terms) => string.Join(" ", terms.Where(term => term.Length > 0));

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
        string name, Func<Task<T>> search, T fallback, CancellationToken token)
    {
        try { return new ProviderResult<T>(await search().ConfigureAwait(false)); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
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
        string query, CancellationToken token)
    {
        var albums = new List<MusicSearchResult>();
        var artists = new List<MusicSearchResult>();

        using var doc = await GetJsonAsync(
            $"{DeezerApi}/search/album?limit=100&q={Uri.EscapeDataString(query)}", token).ConfigureAwait(false);
        
        foreach (var item in ReadData(doc.RootElement))
        {
            var alb = AlbumResult(item);
            if (alb.DeezerId > 0) albums.Add(alb);

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
                    });
                }
            }
        }
        return (albums, artists);
    }

    private static async Task<(List<MusicSearchResult> Tracks, List<MusicSearchResult> Artists, List<MusicSearchResult> Albums)> SearchDeezerTracksAsync(
        string query, CancellationToken token)
    {
        var tracks = new List<MusicSearchResult>();
        var artists = new List<MusicSearchResult>();
        var albums = new List<MusicSearchResult>();

        using var doc = await GetJsonAsync(
            $"{DeezerApi}/search/track?limit=100&q={Uri.EscapeDataString(query)}", token).ConfigureAwait(false);
        
        foreach (var item in ReadData(doc.RootElement))
        {
            var trk = TrackResult(item, "", "", "");
            if (trk.DeezerId > 0) tracks.Add(trk);

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
        string query, string trackQuery, CancellationToken token)
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

            var (artist, title) = ParseYouTubeTitle(Text(item, "title", "Untitled"), channel, trackQuery);
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

    internal static IReadOnlyList<MusicSearchResult> MergeTracks(
        IEnumerable<MusicSearchResult> deezer,
        IEnumerable<MusicSearchResult> youtube)
    {
        // Keep versions on different albums. Prefer the expanded target
        // album when attaching a video with the same song and artist.
        var merged = deezer.DistinctBy(result => (SongKey(result), Normalize(result.Album))).ToList();
        var bySong = merged.ToLookup(SongKey);
        var seenVideos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenSongs = new HashSet<(string Artist, string Title)>();
        foreach (var result in youtube)
        {
            var key = SongKey(result);
            if (!seenVideos.Add(result.YouTubeUrl) || !seenSongs.Add(key)) continue;
            var match = bySong[key].FirstOrDefault();
            if (match is not null) match.MergeFrom(result);
            else merged.Add(result);
        }
        return merged;
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

    internal static (string artist, string title) ParseYouTubeTitle(string raw, string channel, string trackQuery)
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
            // A cover may credit the original artist in the video title.
            // Keep the uploader as the artist when that credit doesn't match.
            if (ExactTrackTitle(trackQuery, right)) return (channel, right);
            if (ExactTrackTitle(trackQuery, left)) return (channel, left);
        }
        return (channel, title);
    }

    internal static IEnumerable<MusicSearchResult> RankResults(IEnumerable<MusicSearchResult> results,
        string trackQuery, string albumQuery, string artistQuery)
    {
        var hasTrack = !string.IsNullOrWhiteSpace(trackQuery);
        var hasAlbum = !string.IsNullOrWhiteSpace(albumQuery);
        var hasArtist = !string.IsNullOrWhiteSpace(artistQuery);
        var words = KeyChars.Replace(JoinTerms(trackQuery, albumQuery, artistQuery).ToLowerInvariant(), " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
        return results.OrderBy(result =>
        {
            var title = result.Kind == MusicSearchResultKind.Track ? result.Title : "";
            var album = result.Kind == MusicSearchResultKind.Album ? result.Title : result.Album;
            var artist = result.Kind == MusicSearchResultKind.Artist ? result.Title : result.Artist;
            var exactTitle = ExactTrackTitle(trackQuery, title);
            var exactAlbum = Equivalent(albumQuery, album);
            var exactArtist = Equivalent(artistQuery, artist);

            // A score can break ties inside a tier, never lift a partial
            // match above an exact match on the requested song or album.
            var tier = 4;
            if (exactTitle && (!hasAlbum || exactAlbum) && (!hasArtist || exactArtist)) tier = 0;
            else if (exactAlbum && (!hasArtist || exactArtist)) tier = 1;
            else if (exactTitle) tier = 2;
            else if (exactArtist) tier = 3;

            var kindOrder = 0;
            if (tier == 1)
            {
                var prefer = hasTrack ? MusicSearchResultKind.Track : MusicSearchResultKind.Album;
                kindOrder = result.Kind == prefer ? 0 : 1;
            }
            else if (tier == 3 && !hasTrack && !hasAlbum)
                kindOrder = result.Kind == MusicSearchResultKind.Artist ? 0 : 1;

            var fieldScore = FieldScore(trackQuery, title) + FieldScore(albumQuery, album) + FieldScore(artistQuery, artist);
            var searchable = $"{result.Title} {album} {artist}";
            var wordCount = words.Count(word => searchable.Contains(word, StringComparison.OrdinalIgnoreCase));
            return (tier, kindOrder, -fieldScore, -wordCount, !result.HasDeezer);
        });
    }

    private static int FieldScore(string query, string field)
    {
        var target = Normalize(query);
        if (target.Length == 0) return 0;
        var value = Normalize(field);
        if (value == target) return 4;
        if (value.StartsWith(target, StringComparison.Ordinal)) return 3;
        return value.Contains(target, StringComparison.Ordinal) ? 2 : 0;
    }

    private static bool ExactTrackTitle(string query, string title) => Equivalent(query, title)
        || Equivalent(query, TrackSubtitle.Replace(title, ""));

    private static bool Equivalent(string left, string right) =>
        Normalize(left) is { Length: > 0 } key && key == Normalize(right);

    private static (string Artist, string Title) SongKey(MusicSearchResult result) =>
        (Normalize(result.Artist), Normalize(result.Title));

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
