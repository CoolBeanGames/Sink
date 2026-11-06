using System.IO;
using Sink.Models;
using Sink.Services.Ipod;

namespace Sink.Services;

internal static class LibraryStorage
{
    private sealed record FileSize(string Path, long Bytes);

    public static string Describe(string musicFolder, string originalFolder, IReadOnlyList<Track> tracks)
    {
        var known = tracks.Select(t => t.FilePath).Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.GetFullPath(p!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = known.Select(ReadFile).OfType<FileSize>().ToList();
        var originals = tracks.Select(t => t.OriginalPath).Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.GetFullPath(p!)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var missingOriginals = originals.Where(p => !File.Exists(p)).ToList();
        var lines = new List<string>
        {
            $"Library database · {tracks.Count:N0} tracks",
            $"Library files available · {Format(files)}",
            $"Library file paths unavailable · {known.Count - files.Count:N0}",
        };
        if (files.Count > 0)
            lines.Add("Library formats · " + string.Join(", ", files.GroupBy(f => Path.GetExtension(f.Path).ToUpperInvariant())
                .OrderByDescending(g => g.Count()).Select(g => $"{g.Count():N0} {g.Key.TrimStart('.')}")));
        if (ReadFolder(musicFolder) is { } music)
        {
            lines.Add($"\nMusic folder · {Format(music)}");
            lines.Add($"Audio in Music outside the library database · {Format(music.Where(f => !known.Contains(f.Path) && MusicImporter.IsAudioFile(f.Path)).ToList())}");
            lines.Add($"Other files in Music · {Format(music.Where(f => !known.Contains(f.Path) && !MusicImporter.IsAudioFile(f.Path)).ToList())}");
            var musicPaths = music.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var candidates = missingOriginals.Count(p => MusicImporter.IsUnder(p, originalFolder)
                && musicPaths.Contains(Path.GetFullPath(Path.Combine(musicFolder, Path.GetRelativePath(originalFolder, p)))));
            if (candidates > 0)
                lines.Add($"Missing originals with the same relative filename under Music · {candidates:N0}");
        }
        else lines.Add("\nMusic folder · unavailable");
        lines.Add(ReadFolder(originalFolder) is { } originalsOnDisk
            ? $"Originals folder · {Format(originalsOnDisk)}" : "Originals folder · unavailable");
        lines.Add($"Original file references unavailable · {missingOriginals.Count:N0} of {originals.Count:N0}");
        if (!File.Exists(Download.ToolManager.FfmpegPath))
        {
            var conversion = files.Count(f => IpodTranscoder.NeedsTranscode(f.Path));
            if (conversion > 0) lines.Add($"\niPod sync needs ffmpeg to convert {conversion:N0} of the available library files.");
        }
        return string.Join("\n", lines);
    }

    private static string Format(IReadOnlyCollection<FileSize> files) =>
        $"{files.Count:N0} file{(files.Count == 1 ? "" : "s")}, {files.Sum(f => f.Bytes) / 1_000_000_000d:N2} GB";

    private static FileSize? ReadFile(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? new(file.FullName, file.Length) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static List<FileSize>? ReadFolder(string root)
    {
        if (!Directory.Exists(root)) return null;
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false,
            };
            return new DirectoryInfo(root).EnumerateFiles("*", options).Select(f => new FileSize(f.FullName, f.Length)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
