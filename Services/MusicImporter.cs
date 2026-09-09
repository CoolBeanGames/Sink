using System.IO;
using System.Text.RegularExpressions;
using Sink.Models;

namespace Sink.Services;

public static partial class MusicImporter
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".aac", ".wav", ".wma", ".flac", ".ogg", ".opus"
    };

    public static IReadOnlyList<Track> Import(IEnumerable<string> droppedPaths)
    {
        return ExpandPaths(droppedPaths).Distinct(StringComparer.OrdinalIgnoreCase).Select(CreateTrack).ToList();
    }

    private static IEnumerable<string> ExpandPaths(IEnumerable<string> droppedPaths)
    {
        var directories = new Stack<string>();
        foreach (var path in droppedPaths)
        {
            if (File.Exists(path) && AudioExtensions.Contains(Path.GetExtension(path))) yield return path;
            else if (Directory.Exists(path)) directories.Push(path);
        }

        while (directories.Count > 0)
        {
            var directory = directories.Pop();
            IEnumerable<string> files;
            IEnumerable<string> children;
            try
            {
                files = Directory.EnumerateFiles(directory).ToList();
                children = Directory.EnumerateDirectories(directory).ToList();
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var file in files.Where(file => AudioExtensions.Contains(Path.GetExtension(file)))) yield return file;
            foreach (var child in children) directories.Push(child);
        }
    }

    private static Track CreateTrack(string filePath)
    {
        var file = new FileInfo(filePath);
        var baseName = Path.GetFileNameWithoutExtension(file.Name);
        var numberMatch = LeadingTrackNumber().Match(baseName);
        var title = CleanSeparators().Replace(LeadingTrackNumber().Replace(baseName, ""), " ").Trim();
        return new Track
        {
            Title = string.IsNullOrWhiteSpace(title) ? baseName : title,
            Artist = "Unknown Artist",
            Album = file.Directory?.Name ?? "Imported Music",
            Genre = "Unknown",
            FileName = file.Name,
            FilePath = file.FullName,
            TrackNumber = numberMatch.Success && int.TryParse(numberMatch.Groups[1].Value, out var number) ? number : 0,
            Year = file.LastWriteTime.Year,
            Duration = TimeSpan.Zero
        };
    }

    [GeneratedRegex(@"^(\d+)[\s._-]*")]
    private static partial Regex LeadingTrackNumber();

    [GeneratedRegex(@"[_-]+")]
    private static partial Regex CleanSeparators();
}
