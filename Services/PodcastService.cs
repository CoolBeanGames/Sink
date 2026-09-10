using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Xml.Linq;
using Sink.Models;

namespace Sink.Services;

public sealed record PodcastSearchResult(
    string Title, string Author, string FeedUrl, string? ArtworkUrl, int EpisodeCount, DateTime? LastRelease);

/// <summary>
/// Podcast directory search (iTunes Search API), RSS feed parsing, and episode
/// downloads. No API key required.
/// </summary>
public static class PodcastService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Sink/1.0");
        return c;
    }

    public static async Task<IReadOnlyList<PodcastSearchResult>> SearchAsync(string term, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(term)) return [];
        var url = $"https://itunes.apple.com/search?media=podcast&limit=25&term={Uri.EscapeDataString(term)}";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, token).ConfigureAwait(false));
        var results = new List<PodcastSearchResult>();
        if (!doc.RootElement.TryGetProperty("results", out var arr)) return results;
        foreach (var r in arr.EnumerateArray())
        {
            var feed = Get(r, "feedUrl");
            if (string.IsNullOrWhiteSpace(feed)) continue;
            DateTime? last = DateTime.TryParse(Get(r, "releaseDate"), out var d) ? d : null;
            results.Add(new PodcastSearchResult(
                Get(r, "collectionName") ?? "Untitled",
                Get(r, "artistName") ?? "",
                feed,
                Get(r, "artworkUrl600") ?? Get(r, "artworkUrl100"),
                r.TryGetProperty("trackCount", out var tc) && tc.ValueKind == JsonValueKind.Number ? tc.GetInt32() : 0,
                last));
        }
        return results;
    }

    /// <summary>Fetches and parses an RSS feed into a Podcast with its episodes (newest first).</summary>
    public static async Task<Podcast> LoadFeedAsync(string feedUrl, CancellationToken token = default)
    {
        var xml = await Http.GetStringAsync(feedUrl, token).ConfigureAwait(false);
        var doc = XDocument.Parse(xml);
        XNamespace itunes = "http://www.itunes.com/dtds/podcast-1.0.dtd";
        var channel = doc.Root?.Element("channel") ?? throw new InvalidOperationException("Not an RSS feed");

        var podcast = new Podcast
        {
            Title = (string?)channel.Element("title") ?? "Untitled",
            Author = (string?)channel.Element(itunes + "author") ?? "",
            FeedUrl = feedUrl,
            Description = Trim((string?)channel.Element("description")),
            ArtworkUrl = (string?)channel.Element(itunes + "image")?.Attribute("href")
                         ?? (string?)channel.Element("image")?.Element("url"),
        };

        var items = channel.Elements("item").ToList();
        foreach (var item in items)
        {
            var enclosure = item.Element("enclosure");
            var audio = (string?)enclosure?.Attribute("url");
            if (string.IsNullOrWhiteSpace(audio)) continue;

            DateTime.TryParse((string?)item.Element("pubDate"), out var published);
            var durationText = (string?)item.Element(itunes + "duration");
            var epNo = int.TryParse((string?)item.Element(itunes + "episode"), out var e) ? e : 0;

            podcast.Episodes.Add(new PodcastEpisode
            {
                EpisodeGuid = (string?)item.Element("guid") ?? audio,
                Title = Trim((string?)item.Element("title")),
                Description = Trim((string?)item.Element("description")),
                Published = published,
                AudioUrl = audio,
                EpisodeNumber = epNo,
                Duration = ParseDuration(durationText),
            });
        }

        // Newest first; number them from the end so oldest = 1 when the feed omits it.
        podcast.Episodes = podcast.Episodes.OrderByDescending(x => x.Published).ToList();
        for (var i = 0; i < podcast.Episodes.Count; i++)
            if (podcast.Episodes[i].EpisodeNumber == 0)
                podcast.Episodes[i].EpisodeNumber = podcast.Episodes.Count - i;

        return podcast;
    }

    /// <summary>
    /// Merges a freshly fetched feed into an existing subscription, keeping play
    /// state and local downloads for episodes matched by guid.
    /// </summary>
    public static void MergeFeed(Podcast existing, Podcast fresh)
    {
        existing.Title = fresh.Title;
        existing.Author = fresh.Author;
        existing.Description = fresh.Description;
        existing.ArtworkUrl = fresh.ArtworkUrl ?? existing.ArtworkUrl;

        var byGuid = existing.Episodes.ToDictionary(e => e.EpisodeGuid, e => e);
        foreach (var episode in fresh.Episodes)
        {
            if (byGuid.TryGetValue(episode.EpisodeGuid, out var have))
            {
                have.Title = episode.Title;
                have.Description = episode.Description;
                have.Published = episode.Published;
                have.AudioUrl = episode.AudioUrl;
                have.EpisodeNumber = episode.EpisodeNumber;
                if (have.Duration <= TimeSpan.Zero) have.Duration = episode.Duration;
            }
            else
            {
                existing.Episodes.Add(episode);
            }
        }
        existing.Episodes = existing.Episodes.OrderByDescending(e => e.Published).ToList();
    }

    public static async Task<string> DownloadEpisodeAsync(
        PodcastEpisode episode, string directory, IProgress<double>? progress, CancellationToken token = default)
    {
        Directory.CreateDirectory(directory);
        var ext = SafeExtension(episode.AudioUrl);
        var path = System.IO.Path.Combine(directory, Sanitize($"{episode.EpisodeNumber:D4} - {episode.Title}") + ext);

        using var response = await Http.GetAsync(episode.AudioUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? -1;

        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var dest = File.Create(path);
        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            await dest.WriteAsync(buffer.AsMemory(0, n), token).ConfigureAwait(false);
            read += n;
            if (total > 0) progress?.Report((double)read / total);
        }
        progress?.Report(1);
        return path;
    }

    private static string SafeExtension(string url)
    {
        try
        {
            var ext = System.IO.Path.GetExtension(new Uri(url).AbsolutePath);
            return ext is ".mp3" or ".m4a" or ".aac" or ".ogg" or ".opus" or ".wav" ? ext : ".mp3";
        }
        catch { return ".mp3"; }
    }

    private static TimeSpan ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return TimeSpan.Zero;
        text = text.Trim();
        if (int.TryParse(text, out var seconds)) return TimeSpan.FromSeconds(seconds);
        var parts = text.Split(':');
        try
        {
            return parts.Length switch
            {
                3 => new TimeSpan(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2])),
                2 => new TimeSpan(0, int.Parse(parts[0]), int.Parse(parts[1])),
                _ => TimeSpan.Zero,
            };
        }
        catch { return TimeSpan.Zero; }
    }

    private static string Trim(string? s) => (s ?? "").Trim();

    private static string Sanitize(string name)
    {
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length > 120 ? name[..120] : name;
    }

    private static string? Get(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
