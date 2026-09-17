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

public sealed class MusicSearchResult : INotifyPropertyChanged
{
    private string _artist = "";
    private string _album = "";
    private string _genre = "Unknown";
    private string _artwork = "";

    public required string Title { get; init; }
    public string Artist { get => _artist; set { if (Set(ref _artist, value)) OnPropertyChanged(nameof(Subtitle)); } }
    public string Album { get => _album; set { if (Set(ref _album, value)) OnPropertyChanged(nameof(Subtitle)); } }
    public string Genre { get => _genre; set => Set(ref _genre, value); }
    public string Artwork { get => _artwork; set => Set(ref _artwork, value); }
    public string YouTubeUrl { get; internal set; } = "";
    public string DeezerUrl { get; internal set; } = "";

    /// <summary>
    /// Spotify's anonymous search endpoint is no longer available. Sink's
    /// existing Spotify workflow resolves public Spotify metadata and matches
    /// the audio by artist/title through YouTube, so text-search results expose
    /// that same keyless matcher whenever they have both fields.
    /// </summary>
    public bool HasSpotify => !string.IsNullOrWhiteSpace(Title) && !string.IsNullOrWhiteSpace(Artist);
    public bool HasYouTube => !string.IsNullOrWhiteSpace(YouTubeUrl);
    public bool HasDeezer => !string.IsNullOrWhiteSpace(DeezerUrl);
    public string Subtitle => string.Join("  ·  ", new[] { Artist, Album }.Where(s => !string.IsNullOrWhiteSpace(s)));
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
        if (Genre is "" or "Unknown" && other.Genre is not ("" or "Unknown")) Genre = other.Genre;
        if (string.IsNullOrWhiteSpace(Artwork)) Artwork = other.Artwork;
        if (string.IsNullOrWhiteSpace(YouTubeUrl)) YouTubeUrl = other.YouTubeUrl;
        if (string.IsNullOrWhiteSpace(DeezerUrl)) DeezerUrl = other.DeezerUrl;
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
    IReadOnlyList<MusicSearchResult> Results, IReadOnlyList<string> ProviderErrors);

/// <summary>Keyless Deezer and YouTube search, merged into editable track rows.</summary>
public static class MusicSearchService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly Regex Noise = new(
        @"\s*[\[(](?:official\s+(?:music\s+)?video|audio|lyrics?|visuali[sz]er)[^\])]*[\])]\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex KeyChars = new(@"[^a-z0-9]+", RegexOptions.Compiled);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Sink/1.0 (+https://github.com)");
        return client;
    }

    public static async Task<MusicSearchResponse> SearchAsync(
        string query, CancellationToken token = default)
    {
        var deezerTask = SafeProviderAsync("Deezer", () => SearchDeezerAsync(query, token));
        var youtubeTask = SafeProviderAsync("YouTube", () => SearchYouTubeAsync(query, token));
        var providerResults = await Task.WhenAll(deezerTask, youtubeTask).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        var merged = new Dictionary<string, MusicSearchResult>(StringComparer.Ordinal);
        foreach (var provider in providerResults)
        {
            foreach (var result in provider.Results)
            {
                var key = Key(result.Artist, result.Title);
                if (!merged.TryGetValue(key, out var existing)) merged[key] = result;
                else existing.MergeFrom(result);
            }
        }

        var ordered = merged.Values
            .OrderByDescending(r => r.ProviderCount)
            .ThenByDescending(r => r.HasDeezer)
            .ThenBy(r => r.Artist, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .Take(24)
            .ToList();
        return new MusicSearchResponse(
            ordered,
            providerResults.Where(r => r.Error is not null).Select(r => r.Error!).ToList());
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

    private sealed record ProviderResult(IReadOnlyList<MusicSearchResult> Results, string? Error = null);

    private static async Task<ProviderResult> SafeProviderAsync(
        string name, Func<Task<IReadOnlyList<MusicSearchResult>>> search)
    {
        try { return new ProviderResult(await search().ConfigureAwait(false)); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"{name} music search failed: {ex.Message}");
            return new ProviderResult([], $"{name}: {ex.Message}");
        }
    }

    private static async Task<IReadOnlyList<MusicSearchResult>> SearchDeezerAsync(
        string query, CancellationToken token)
    {
        var url = "https://api.deezer.com/search?limit=14&q=" + Uri.EscapeDataString(query);
        using var response = await Http.GetAsync(url, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        if (doc.RootElement.TryGetProperty("error", out var error))
            throw new InvalidOperationException(Text(error, "message", "Deezer search failed"));
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        var results = new List<MusicSearchResult>();
        foreach (var item in data.EnumerateArray())
        {
            var id = Number(item, "id");
            if (id <= 0) continue;
            results.Add(new MusicSearchResult
            {
                Title = Text(item, "title", "Untitled"),
                Artist = NestedText(item, "artist", "name", "Unknown Artist"),
                Album = NestedText(item, "album", "title", ""),
                Genre = "Unknown",
                Artwork = NestedText(item, "album", "cover_xl",
                    NestedText(item, "album", "cover_medium", "")),
                DeezerUrl = $"https://www.deezer.com/track/{id}",
            });
        }
        return results;
    }

    private static async Task<IReadOnlyList<MusicSearchResult>> SearchYouTubeAsync(
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
        foreach (var argument in new[] { "-J", "--flat-playlist", "--no-warnings", "ytsearch14:" + query })
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
            return [];
        var results = new List<MusicSearchResult>();
        foreach (var item in entries.EnumerateArray())
        {
            var id = Text(item, "id", "");
            if (id.Length == 0) continue;
            var title = Noise.Replace(Text(item, "title", "Untitled"), "").Trim();
            var artist = Text(item, "artist",
                Text(item, "uploader", Text(item, "channel", "Unknown Artist")));
            var artwork = Text(item, "thumbnail", "");
            results.Add(new MusicSearchResult
            {
                Title = title,
                Artist = artist.Replace(" - Topic", "", StringComparison.OrdinalIgnoreCase).Trim(),
                Album = "",
                Genre = "Unknown",
                Artwork = artwork,
                YouTubeUrl = $"https://www.youtube.com/watch?v={id}",
            });
        }
        return results;
    }

    private static string Key(string artist, string title)
    {
        var cleanTitle = KeyChars.Replace(Noise.Replace(title, "").ToLowerInvariant(), "");
        var cleanArtist = KeyChars.Replace(artist.Replace(" - Topic", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant(), "");
        return cleanArtist + "|" + cleanTitle;
    }

    private static string Text(JsonElement root, string property, string fallback) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() is { Length: > 0 } text ? text : fallback
            : fallback;

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
