using System.IO;
using Sink.Models;

namespace Sink.Services;

internal static class PodcastDownloads
{
    /// <summary>Reconnect moved files and recover downloads made before Sink stored the catalog beside them.</summary>
    public static bool TryRecover(string root, List<Podcast> shows, out bool changed, IReadOnlySet<string>? removedDownloads = null)
    {
        changed = false;
        var folders = new List<(string Name, string[] Files)>();
        try
        {
            var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
            foreach (var directory in Directory.EnumerateDirectories(root, "*", options))
            {
                var files = Directory.EnumerateFiles(directory, "*", options)
                    .Where(path => TryReadName(path, out _, out _)
                        && (removedDownloads is null || !removedDownloads.Contains(Path.GetRelativePath(root, path).Replace('\\', '/')))).ToArray();
                if (files.Length > 0) folders.Add((Path.GetFileName(directory), files));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Info($"Podcast folder could not be scanned: {ex.Message}");
            return false;
        }

        foreach (var (name, files) in folders)
        {
            var matchingShows = shows.Where(show => string.Equals(PodcastService.ShowFolderName(show.Title), name, StringComparison.OrdinalIgnoreCase)
                || show.Episodes.Any(ep => string.Equals(Path.GetFileName(Path.GetDirectoryName(ep.LocalPath)), name, StringComparison.OrdinalIgnoreCase))).ToList();
            var show = matchingShows.FirstOrDefault();
            if (show is null)
            {
                show = new Podcast { Title = name, RuleCount = 0 };
                shows.Add(show);
                matchingShows.Add(show);
            }

            var byFile = new Dictionary<string, PodcastEpisode>(StringComparer.OrdinalIgnoreCase);
            foreach (var episode in matchingShows.SelectMany(p => p.Episodes))
            {
                if (Path.GetFileName(episode.LocalPath) is { Length: > 0 } saved) byFile.TryAdd(saved, episode);
                byFile.TryAdd(PodcastService.DownloadFileName(episode), episode);
            }
            foreach (var path in files)
            {
                var file = Path.GetFileName(path);
                if (!byFile.TryGetValue(file, out var episode))
                {
                    TryReadName(path, out var number, out var title);
                    episode = new PodcastEpisode { EpisodeGuid = "download:" + name + "/" + file, Title = title, EpisodeNumber = number };
                    show.Episodes.Add(episode);
                    byFile.Add(file, episode);
                }
                if (episode.IsDownloaded) continue;
                episode.LocalPath = path;
                changed = true;
            }
        }
        return true;
    }

    private static bool TryReadName(string path, out int number, out string title)
    {
        number = 0;
        title = "";
        if (!PodcastService.IsAudioExtension(Path.GetExtension(path))) return false;
        var name = Path.GetFileNameWithoutExtension(path);
        var separator = name.IndexOf(" - ", StringComparison.Ordinal);
        if (separator < 4 || !int.TryParse(name.AsSpan(0, separator), out number)) return false;
        title = name[(separator + 3)..];
        return title.Length > 0;
    }
}
