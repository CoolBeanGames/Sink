using System.IO;
using Sink.Models;

namespace Sink.Services;

/// <summary>
/// Per-show download rules: keep <see cref="Podcast.RuleCount"/> unplayed episodes
/// downloaded from the newest (or oldest) end of the feed. Playing an episode
/// deletes its download and lets an older one take its place. An episode counts
/// as played at 90% listened, or at 20% once its download is a week old.
/// </summary>
public static class PodcastRules
{
    public static bool ShouldMarkPlayed(PodcastEpisode e)
    {
        if (e.IsPlayed) return true;
        if (e.Duration <= TimeSpan.Zero) return false;
        var progress = e.PositionSeconds / e.Duration.TotalSeconds;
        if (progress >= 0.90) return true;
        var stale = e.DownloadedAt is { } d && (DateTime.UtcNow - d) >= TimeSpan.FromDays(7);
        return stale && progress >= 0.20;
    }

    /// <summary>Episodes that should be downloaded now to satisfy the show's rule.</summary>
    public static IReadOnlyList<PodcastEpisode> DesiredDownloads(Podcast podcast)
    {
        if (podcast.RuleCount <= 0) return [];
        var ordered = podcast.RuleMode == PodcastRuleMode.Newest
            ? podcast.Episodes.OrderByDescending(e => e.Published)
            : podcast.Episodes.OrderBy(e => e.Published);

        var have = 0;
        var want = new List<PodcastEpisode>();
        foreach (var episode in ordered)
        {
            if (episode.IsPlayed) continue;
            if (episode.IsDownloaded) { have++; }
            else if (have + want.Count < podcast.RuleCount) want.Add(episode);
            if (have + want.Count >= podcast.RuleCount) break;
        }
        return want;
    }

    /// <summary>Deletes the local file for a played episode (called after marking it played).</summary>
    public static void DropDownload(PodcastEpisode episode)
    {
        var path = episode.LocalPath;
        episode.LocalPath = null;
        episode.DownloadedAt = null;
        if (string.IsNullOrEmpty(path)) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Re-evaluates played state for every episode and drops downloads for the
    /// ones that just became played. Returns true if anything changed.
    /// </summary>
    public static bool Reconcile(Podcast podcast)
    {
        var changed = false;
        foreach (var episode in podcast.Episodes)
        {
            if (episode.IsPlayed || !ShouldMarkPlayed(episode)) continue;
            episode.IsPlayed = true;
            if (episode.IsDownloaded) DropDownload(episode);
            changed = true;
        }
        return changed;
    }
}
