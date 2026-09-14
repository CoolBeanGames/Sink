using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Sink.Services;

/// <summary>
/// Best-effort metadata lookup for tracks missing Album/Genre/Artist/Year
/// (task 160). Uses Apple's keyless iTunes Search API rather than e.g.
/// MusicBrainz specifically because one request returns everything needed
/// at once (album, genre, year, track number) — the task explicitly calls
/// out rate limiting as the thing to watch for on whatever API gets
/// picked, and the fewer requests per track, the safer. Every call is
/// paced through a shared gate so a bulk auto-tag never bursts faster
/// than one lookup per ~1.2s, comfortably under Apple's informal
/// unauthenticated-search guidance (~20/minute).
/// </summary>
public static class AutoTagService
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
    })
    { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(1200);
    private static DateTime _lastRequestUtc = DateTime.MinValue;

    public readonly record struct Result(bool Found, string? Album, string? Genre, string? Artist, int Year, int TrackNumber);

    /// <summary>True when a field still holds the app's own "nothing set yet" placeholder — safe to overwrite; anything else is a real user/import value and must be left alone.</summary>
    public static bool IsUnknown(string? value, string placeholder) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value, placeholder, StringComparison.OrdinalIgnoreCase);

    public static async Task<Result> LookupAsync(string title, string artist, CancellationToken token = default)
    {
        var none = new Result(false, null, null, null, 0, 0);
        if (string.IsNullOrWhiteSpace(title)) return none;

        await ThrottleAsync(token).ConfigureAwait(false);

        var knownArtist = IsUnknown(artist, "Unknown Artist") ? null : artist;
        var term = knownArtist is null ? title : $"{knownArtist} {title}";
        var url = "https://itunes.apple.com/search?entity=song&limit=1&term=" + Uri.EscapeDataString(term);

        try
        {
            using var response = await Http.GetAsync(url, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return none;
            var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
                return none;

            var hit = results[0];
            string? Str(string prop) => hit.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            int Int(string prop) => hit.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

            var year = 0;
            if (Str("releaseDate") is { Length: >= 4 } date && int.TryParse(date[..4], out var y)) year = y;

            return new Result(true, Str("collectionName"), Str("primaryGenreName"), Str("artistName"), year, Int("trackNumber"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Log.Warn($"Auto-tag lookup failed for \"{title}\": {ex.Message}");
            return none;
        }
    }

    /// <summary>Guarantees at least <see cref="MinInterval"/> between the start of consecutive requests, globally, regardless of how many callers are looking up tracks at once.</summary>
    private static async Task ThrottleAsync(CancellationToken token)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var wait = _lastRequestUtc + MinInterval - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, token).ConfigureAwait(false);
        }
        finally
        {
            _lastRequestUtc = DateTime.UtcNow;
            Gate.Release();
        }
    }
}
