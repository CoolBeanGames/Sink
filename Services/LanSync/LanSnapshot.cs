using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sink.Models;

namespace Sink.Services.LanSync;

internal sealed class LanSnapshot
{
    public List<Track> Tracks { get; set; } = [];
    public List<PlaylistData> Playlists { get; set; } = [];
    public List<Podcast> Podcasts { get; set; } = [];
    public List<ListenEvent> Listens { get; set; } = [];

    public JsonObject Catalog() => new()
    {
        [nameof(Tracks)] = JsonSerializer.SerializeToNode(Tracks),
        [nameof(Playlists)] = JsonSerializer.SerializeToNode(Playlists),
        [nameof(Podcasts)] = JsonSerializer.SerializeToNode(Podcasts),
    };

    public LanSnapshot Copy() => JsonSerializer.Deserialize<LanSnapshot>(JsonSerializer.SerializeToUtf8Bytes(this))!;

    public void Validate()
    {
        UniqueIds(Tracks.Select(t => t.Id));
        UniqueIds(Playlists.Select(p => p.Id));
        UniqueIds(Podcasts.Select(p => p.Id));
        UniqueIds(Podcasts.SelectMany(p => p.Episodes).Select(e => e.Id));
        foreach (var t in Tracks)
        {
            if (t.Title is null || t.Artist is null || t.Album is null || t.Genre is null
                || t.FileName is null || t.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || t.FileName is "." or ".." || t.Duration < TimeSpan.Zero)
                throw new InvalidDataException("The peer sent an invalid music track.");
            LanPaths.Validate(t.FilePath);
            LanPaths.Validate(t.OriginalPath);
            // These are local caches and device bookkeeping, never peer instructions.
            t.ArtworkPath = t.QualityVerdict = t.QualityCheckedPath = t.LastSyncedKey = null;
            t.SyncedIpodPlayCounts = null;
        }
        foreach (var playlist in Playlists)
            if (playlist.Name is null || playlist.TrackIds is null)
                throw new InvalidDataException("The peer sent an invalid playlist.");
        foreach (var podcast in Podcasts)
        {
            if (podcast.Title is null || podcast.Author is null || podcast.Description is null
                || podcast.FeedUrl is null || !Enum.IsDefined(podcast.RuleMode) || podcast.RuleCount < 0)
                throw new InvalidDataException("The peer sent an invalid podcast.");
            ValidateUrl(podcast.FeedUrl);
            ValidateUrl(podcast.ArtworkUrl);
            foreach (var episode in podcast.Episodes)
            {
                if (episode.Title is null || episode.Description is null || episode.EpisodeGuid is null
                    || episode.AudioUrl is null || !double.IsFinite(episode.PositionSeconds)
                    || episode.PositionSeconds < 0 || episode.PositionSeconds > TimeSpan.MaxValue.TotalSeconds
                    || episode.Duration < TimeSpan.Zero || episode.IpodBookmarkMs < 0)
                    throw new InvalidDataException("The peer sent an invalid podcast episode.");
                LanPaths.Validate(episode.LocalPath);
                ValidateUrl(episode.AudioUrl);
            }
        }
        ReflectStore.ValidateHistory(Listens);
    }

    private static void UniqueIds(IEnumerable<Guid> values)
    {
        var seen = new HashSet<Guid>();
        foreach (var id in values)
            if (id == Guid.Empty || !seen.Add(id)) throw new InvalidDataException("The database contains duplicate or missing item IDs.");
    }

    private static void ValidateUrl(string? value)
    {
        if (!string.IsNullOrEmpty(value) && WebUrl(value) is null)
            throw new InvalidDataException("The peer sent a podcast link that isn't a web URL.");
    }

    public static string? WebUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var url)
        && url.Scheme is "https" or "http" ? value : null;
}

internal static class LanPaths
{
    public static string? Relative(string root, string? path)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path)) return null;
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return IsSafe(relative) ? relative : null;
    }

    public static string? Resolve(string root, string? relative)
    {
        Validate(relative);
        return relative is null || string.IsNullOrWhiteSpace(root) ? null : Path.GetFullPath(relative, Path.GetFullPath(root));
    }

    public static void Validate(string? relative)
    {
        if (relative is not null && !IsSafe(relative))
            throw new InvalidDataException("The peer sent a media path outside the configured folder.");
    }

    private static bool IsSafe(string relative) => relative.Split('/').All(part =>
        part.Length > 0 && part is not ("." or "..") && !part.EndsWith('.') && !part.EndsWith(' ')
        && part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !part.Contains('\\'));
}
