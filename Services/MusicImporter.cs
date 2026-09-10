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
        => Import(droppedPaths, AppSettings.Current.ImportMode, AppSettings.Current.LibraryLocation);

    /// <summary>
    /// Imports audio files, optionally relocating them into <paramref name="libraryDir"/>
    /// first when <paramref name="mode"/> is Copy or Move.
    /// </summary>
    public static IReadOnlyList<Track> Import(IEnumerable<string> droppedPaths, ImportMode mode, string libraryDir)
    {
        var files = ExpandPaths(droppedPaths).Distinct(StringComparer.OrdinalIgnoreCase);
        if (mode is ImportMode.Copy or ImportMode.Move)
            files = files.Select(path => Relocate(path, mode, libraryDir)).ToList();
        return files.Select(CreateTrack).ToList();
    }

    private static string Relocate(string sourcePath, ImportMode mode, string libraryDir)
    {
        try
        {
            Directory.CreateDirectory(libraryDir);
            var target = Path.Combine(libraryDir, Path.GetFileName(sourcePath));
            for (var i = 2; File.Exists(target) && !PathsEqual(target, sourcePath); i++)
                target = Path.Combine(libraryDir, $"{Path.GetFileNameWithoutExtension(sourcePath)} ({i}){Path.GetExtension(sourcePath)}");
            if (PathsEqual(target, sourcePath)) return sourcePath;

            if (mode == ImportMode.Move) File.Move(sourcePath, target);
            else File.Copy(sourcePath, target, overwrite: false);
            return target;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return sourcePath; // fall back to referencing in place
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

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
        var titleFromName = CleanSeparators().Replace(LeadingTrackNumber().Replace(baseName, ""), " ").Trim();
        if (string.IsNullOrWhiteSpace(titleFromName)) titleFromName = baseName;

        // Read embedded tags. TagLib throws on unreadable/corrupt files — fall
        // back to the filename-derived values in that case.
        string title = titleFromName, genre = "Unknown";
        string artist = "Unknown Artist";
        string album = file.Directory?.Name ?? "Imported Music";
        int trackNumber = numberMatch.Success && int.TryParse(numberMatch.Groups[1].Value, out var n) ? n : 0;
        int year = file.LastWriteTime.Year;
        var duration = TimeSpan.Zero;
        string? artworkPath = null;

        try
        {
            using var tag = TagLib.File.Create(filePath);
            var t = tag.Tag;
            if (!string.IsNullOrWhiteSpace(t.Title)) title = t.Title.Trim();
            if (!string.IsNullOrWhiteSpace(t.FirstPerformer)) artist = t.FirstPerformer.Trim();
            else if (!string.IsNullOrWhiteSpace(t.FirstAlbumArtist)) artist = t.FirstAlbumArtist.Trim();
            if (!string.IsNullOrWhiteSpace(t.Album)) album = t.Album.Trim();
            if (!string.IsNullOrWhiteSpace(t.FirstGenre)) genre = t.FirstGenre.Trim();
            if (t.Track is > 0 and < int.MaxValue) trackNumber = (int)t.Track;
            if (t.Year is > 0 and < 9999) year = (int)t.Year;
            if (tag.Properties?.Duration > TimeSpan.Zero) duration = tag.Properties.Duration;

            var picture = t.Pictures?.FirstOrDefault(p => p.Data?.Data?.Length > 0);
            if (picture is not null)
                artworkPath = Artwork.Save($"{album}|{artist}", picture.Data.Data, picture.MimeType);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { }

        return new Track
        {
            Title = title,
            Artist = artist,
            Album = album,
            Genre = genre,
            FileName = file.Name,
            FilePath = file.FullName,
            TrackNumber = trackNumber,
            Year = year,
            Duration = duration,
            ArtworkPath = artworkPath
        };
    }

    /// <summary>
    /// Re-reads tags and cover art from a track's file into the existing Track.
    /// Returns false when the file is gone. Used by "Refresh library".
    /// </summary>
    public static bool RefreshTags(Track track)
    {
        if (string.IsNullOrWhiteSpace(track.FilePath) || !File.Exists(track.FilePath)) return false;
        try
        {
            using var tag = TagLib.File.Create(track.FilePath);
            var t = tag.Tag;
            if (!string.IsNullOrWhiteSpace(t.Title)) track.Title = t.Title.Trim();
            if (!string.IsNullOrWhiteSpace(t.FirstPerformer)) track.Artist = t.FirstPerformer.Trim();
            else if (!string.IsNullOrWhiteSpace(t.FirstAlbumArtist)) track.Artist = t.FirstAlbumArtist.Trim();
            if (!string.IsNullOrWhiteSpace(t.Album)) track.Album = t.Album.Trim();
            if (!string.IsNullOrWhiteSpace(t.FirstGenre)) track.Genre = t.FirstGenre.Trim();
            if (t.Track is > 0 and < int.MaxValue) track.TrackNumber = (int)t.Track;
            if (t.Year is > 0 and < 9999) track.Year = (int)t.Year;

            var picture = t.Pictures?.FirstOrDefault(p => p.Data?.Data?.Length > 0);
            if (picture is not null)
                track.ArtworkPath = Artwork.Save($"{track.Album}|{track.Artist}", picture.Data.Data, picture.MimeType);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { }
        return true;
    }

    [GeneratedRegex(@"^(\d+)[\s._-]*")]
    private static partial Regex LeadingTrackNumber();

    [GeneratedRegex(@"[_-]+")]
    private static partial Regex CleanSeparators();
}
