using System.Text.Json.Serialization;

namespace Sink.Models;

public enum PodcastRuleMode { Newest, Oldest }

/// <summary>A subscribed podcast feed plus its episodes and per-show download rule.</summary>
public sealed class Podcast
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string FeedUrl { get; set; } = "";
    public string? ArtworkUrl { get; set; }
    public string Description { get; set; } = "";

    /// <summary>Auto-download this many episodes…</summary>
    public int RuleCount { get; set; } = 3;

    /// <summary>…from the newest or the oldest end of the feed.</summary>
    public PodcastRuleMode RuleMode { get; set; } = PodcastRuleMode.Newest;

    public List<PodcastEpisode> Episodes { get; set; } = [];

    [JsonIgnore] public DateTime? LastPublished => Episodes.Count == 0 ? null : Episodes.Max(e => e.Published);
    [JsonIgnore] public int UnplayedCount => Episodes.Count(e => !e.IsPlayed);

    /// <summary>Show has episodes that arrived since it was last opened and haven't been played.</summary>
    [JsonIgnore] public bool HasNewUnplayed => Episodes.Any(e => e.IsNew && !e.IsPlayed);
}

public sealed class PodcastEpisode
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Stable identity from the RSS &lt;guid&gt; (or the audio URL) so refreshes match.</summary>
    public string EpisodeGuid { get; set; } = "";

    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime Published { get; set; }
    public string AudioUrl { get; set; } = "";
    public int EpisodeNumber { get; set; }
    public TimeSpan Duration { get; set; }

    /// <summary>Set once the episode audio is downloaded locally.</summary>
    public string? LocalPath { get; set; }

    public double PositionSeconds { get; set; }
    public bool IsPlayed { get; set; }

    /// <summary>Arrived on a feed refresh after the show was last opened; drives the "new content" dot.</summary>
    public bool IsNew { get; set; }

    /// <summary>Resume position on the iPod (milliseconds), last seen from the device.</summary>
    public long IpodBookmarkMs { get; set; }

    /// <summary>When the local file was downloaded — drives the "20% after a week" played rule.</summary>
    public DateTime? DownloadedAt { get; set; }

    [JsonIgnore] public bool IsDownloaded => !string.IsNullOrEmpty(LocalPath) && System.IO.File.Exists(LocalPath);

    [JsonIgnore]
    public double Progress => Duration.TotalSeconds > 0 ? Math.Clamp(PositionSeconds / Duration.TotalSeconds, 0, 1) : 0;

    [JsonIgnore]
    public string RemainingText
    {
        get
        {
            if (Duration <= TimeSpan.Zero) return "";
            var left = Duration - TimeSpan.FromSeconds(PositionSeconds);
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            return IsPlayed ? "Played" : left.TotalHours >= 1 ? $"{(int)left.TotalHours}h {left.Minutes}m left" : $"{left.Minutes}m left";
        }
    }
}
