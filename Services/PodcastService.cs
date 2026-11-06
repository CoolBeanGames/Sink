using System.Diagnostics;
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

    internal static async Task<Podcast?> FindRecoveredFeedAsync(Podcast recovered, CancellationToken token)
    {
        var candidates = (await SearchAsync(recovered.Title.Replace('_', ' '), token))
            .Where(result => MatchesShowTitle(result.Title, recovered.Title)
                && Uri.TryCreate(result.FeedUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            .DistinctBy(result => result.FeedUrl)
            .ToList();
        var feeds = new List<Podcast>();
        foreach (var candidate in candidates)
        {
            try
            {
                var feed = await LoadFeedAsync(candidate.FeedUrl, token);
                if (string.IsNullOrWhiteSpace(feed.ArtworkUrl)) feed.ArtworkUrl = candidate.ArtworkUrl;
                if (string.IsNullOrWhiteSpace(feed.Author)) feed.Author = candidate.Author;
                feeds.Add(feed);
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Xml.XmlException or InvalidOperationException)
            {
                // The directory sometimes lists feeds that have moved or shut down.
            }
        }
        return ChooseRecoveredFeed(recovered, feeds, candidates.Count);
    }

    internal static async Task<string?> FindDirectoryArtworkAsync(Podcast podcast, CancellationToken token)
    {
        var results = await SearchAsync(podcast.Title, token);
        return results.FirstOrDefault(result => !string.IsNullOrWhiteSpace(result.ArtworkUrl)
            && string.Equals(result.FeedUrl, podcast.FeedUrl, StringComparison.Ordinal))?.ArtworkUrl;
    }

    internal static Podcast? ChooseRecoveredFeed(Podcast recovered, IReadOnlyList<Podcast> feeds, int candidateCount)
    {
        var bestCount = 0;
        Podcast? best = null;
        foreach (var feed in feeds)
        {
            var matchingEpisodes = recovered.Episodes.Count(local => feed.Episodes.Any(remote => MatchesRecoveredEpisode(local, remote)));
            if (matchingEpisodes == 0 && candidateCount == 1 && MatchesShowTitle(feed.Title, recovered.Title)) return feed;
            if (matchingEpisodes == 0 || matchingEpisodes < bestCount) continue;
            best = matchingEpisodes > bestCount ? feed : null;
            bestCount = matchingEpisodes;
        }
        return best;
    }

    internal static bool MatchesShowTitle(string left, string right) =>
        string.Equals(ShowFolderName(left), ShowFolderName(right), StringComparison.OrdinalIgnoreCase);

    private static bool MatchesRecoveredEpisode(PodcastEpisode local, PodcastEpisode remote)
    {
        if (!local.EpisodeGuid.StartsWith("download:", StringComparison.Ordinal)) return false;
        var fileStem = Path.GetFileNameWithoutExtension(local.EpisodeGuid["download:".Length..]);
        return string.Equals(fileStem, DownloadFileStem(local.EpisodeNumber, remote.Title), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Merges a freshly fetched feed into an existing subscription, keeping play
    /// state and local downloads for episodes matched by guid.
    /// </summary>
    public static void MergeFeed(Podcast existing, Podcast fresh, bool markNew = true)
    {
        existing.Title = fresh.Title;
        existing.FeedUrl = fresh.FeedUrl;
        if (!string.IsNullOrWhiteSpace(fresh.Author)) existing.Author = fresh.Author;
        existing.Description = fresh.Description;
        if (!string.IsNullOrWhiteSpace(fresh.ArtworkUrl)) existing.ArtworkUrl = fresh.ArtworkUrl;

        var byGuid = existing.Episodes.ToLookup(e => e.EpisodeGuid);
        var recovered = existing.Episodes.Where(e => e.EpisodeGuid.StartsWith("download:", StringComparison.Ordinal))
            .ToList();
        var feedEpisodes = fresh.Episodes.DistinctBy(e => e.EpisodeGuid).ToList();
        var feedTitleCounts = recovered.Count > 0
            ? feedEpisodes.ToLookup(e => DownloadFileStem(0, e.Title), StringComparer.OrdinalIgnoreCase) : null;
        foreach (var episode in feedEpisodes)
        {
            var have = byGuid[episode.EpisodeGuid].FirstOrDefault();
            if (have is null && recovered.Count > 0)
            {
                var fileName = DownloadFileName(episode);
                have = recovered.FirstOrDefault(e => string.Equals(Path.GetFileName(e.LocalPath), fileName, StringComparison.OrdinalIgnoreCase));
                if (have is null && feedTitleCounts?[DownloadFileStem(0, episode.Title)].Count() == 1)
                {
                    var matches = recovered.Where(e => MatchesRecoveredEpisode(e, episode)).Take(2).ToList();
                    if (matches.Count == 1) have = matches[0];
                }
                if (have is not null) recovered.Remove(have);
            }
            if (have is not null)
            {
                have.EpisodeGuid = episode.EpisodeGuid;
                have.Title = episode.Title;
                have.Description = episode.Description;
                have.Published = episode.Published;
                have.AudioUrl = episode.AudioUrl;
                have.EpisodeNumber = episode.EpisodeNumber;
                if (have.Duration <= TimeSpan.Zero) have.Duration = episode.Duration;
            }
            else
            {
                episode.IsNew = markNew;
                existing.Episodes.Add(episode);
            }
        }
        existing.Episodes = existing.Episodes.OrderByDescending(e => e.Published).ToList();
    }

    public static async Task<string> DownloadEpisodeAsync(
        PodcastEpisode episode, string directory, IProgress<double>? progress, CancellationToken token = default)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, DownloadFileName(episode));
        if (File.Exists(path))
        {
            progress?.Report(1);
            return path;
        }

        using var response = await Http.GetAsync(episode.AudioUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? -1;

        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        // A folder scan must not pick up an episode whose transfer is still running.
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".part";
        var buffer = new byte[81920];
        long read = 0;
        int n;
        var lastReported = 0d;
        var lastReportAt = Stopwatch.GetTimestamp();
        try
        {
            await using (var dest = File.Create(temporary))
            {
                while ((n = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    await dest.WriteAsync(buffer.AsMemory(0, n), token).ConfigureAwait(false);
                    read += n;
                    if (total <= 0) continue;
                    var value = Math.Clamp((double)read / total, 0, 1);
                    // Limit UI reports during large transfers.
                    if (value - lastReported >= 0.01
                        || Stopwatch.GetElapsedTime(lastReportAt) >= TimeSpan.FromMilliseconds(125))
                    {
                        progress?.Report(value);
                        lastReported = value;
                        lastReportAt = Stopwatch.GetTimestamp();
                    }
                }
            }
            if (read == 0) throw new IOException("The podcast download was empty.");
            File.Move(temporary, path);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        progress?.Report(1);
        return path;
    }

    internal static string DownloadFileName(PodcastEpisode episode) =>
        DownloadFileStem(episode.EpisodeNumber, episode.Title) + SafeExtension(episode.AudioUrl);

    internal static string DownloadFileStem(int number, string title) => Sanitize($"{number:D4} - {title}");

    internal static string ShowFolderName(string title)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) title = title.Replace(c, '_');
        return title.Trim();
    }

    internal static bool IsAudioExtension(string extension) =>
        extension.ToLowerInvariant() is ".mp3" or ".m4a" or ".aac" or ".ogg" or ".opus" or ".wav";

    private static string SafeExtension(string url)
    {
        try
        {
            var ext = System.IO.Path.GetExtension(new Uri(url).AbsolutePath);
            return IsAudioExtension(ext) ? ext : ".mp3";
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
