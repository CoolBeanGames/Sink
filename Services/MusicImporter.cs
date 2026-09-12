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

    /// <summary>
    /// The Artist/Album subfolder a track belongs in under the library root
    /// (task 155) — blank input falls back to "Unknown Artist"/"Unknown Album"
    /// rather than an invalid or missing path segment.
    /// </summary>
    public static string ArtistAlbumDir(string libraryDir, string? artist, string? album) =>
        Path.Combine(libraryDir,
            SanitizeSegment(string.IsNullOrWhiteSpace(artist) ? "Unknown Artist" : artist),
            SanitizeSegment(string.IsNullOrWhiteSpace(album) ? "Unknown Album" : album));

    /// <summary>Makes a string safe to use as one path segment (folder or file name), capped to a sane length.</summary>
    public static string SanitizeSegment(string name)
    {
        var trimmed = name.Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) trimmed = trimmed.Replace(c, '_');
        return trimmed.Length > 120 ? trimmed[..120] : trimmed;
    }

    private static string Relocate(string sourcePath, ImportMode mode, string libraryDir)
    {
        try
        {
            var (artist, album) = ReadArtistAlbum(sourcePath);
            var destDir = ArtistAlbumDir(libraryDir, artist, album);
            Directory.CreateDirectory(destDir);
            var target = Path.Combine(destDir, Path.GetFileName(sourcePath));
            for (var i = 2; File.Exists(target) && !PathsEqual(target, sourcePath); i++)
                target = Path.Combine(destDir, $"{Path.GetFileNameWithoutExtension(sourcePath)} ({i}){Path.GetExtension(sourcePath)}");
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

    /// <summary>Best-effort Artist/Album straight from a file's own tags — "Unknown" if unreadable.</summary>
    private static (string Artist, string Album) ReadArtistAlbum(string filePath)
    {
        try
        {
            using var tag = TagLib.File.Create(filePath);
            var t = tag.Tag;
            var artist = !string.IsNullOrWhiteSpace(t.FirstPerformer) ? t.FirstPerformer.Trim()
                : !string.IsNullOrWhiteSpace(t.FirstAlbumArtist) ? t.FirstAlbumArtist.Trim() : "Unknown Artist";
            var album = !string.IsNullOrWhiteSpace(t.Album) ? t.Album.Trim() : "Unknown Album";
            return (artist, album);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return ("Unknown Artist", "Unknown Album");
        }
    }

    public sealed record OrganizeResult(int Moved, int FoldersRemoved);

    /// <summary>
    /// Reorganizes every audio file already under <paramref name="libraryDir"/> into
    /// Artist/Album subfolders (task 155) — keyed off a matching <see cref="Track"/>'s
    /// own Artist/Album when one exists (updating its FilePath/FileName so playback
    /// and iPod sync keep working), or the file's own tags for a loose file nothing
    /// in the library references yet. Files already in the right place are left
    /// alone. Finishes by deleting every folder the moves left empty.
    /// </summary>
    public static OrganizeResult Organize(string libraryDir, IReadOnlyList<Track> tracks)
    {
        if (!Directory.Exists(libraryDir)) return new OrganizeResult(0, 0);
        var byPath = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tracks)
            if (!string.IsNullOrWhiteSpace(t.FilePath))
                byPath[Path.GetFullPath(t.FilePath)] = t;

        var moved = 0;
        foreach (var file in Directory.EnumerateFiles(libraryDir, "*", SearchOption.AllDirectories).ToList())
        {
            if (!AudioExtensions.Contains(Path.GetExtension(file))) continue;
            var full = Path.GetFullPath(file);
            var track = byPath.GetValueOrDefault(full);
            var (artist, album) = track is not null ? (track.Artist, track.Album) : ReadArtistAlbum(file);
            var destDir = ArtistAlbumDir(libraryDir, artist, album);
            var dest = Path.Combine(destDir, Path.GetFileName(file));
            if (PathsEqual(dest, file)) continue;

            try
            {
                Directory.CreateDirectory(destDir);
                for (var i = 2; File.Exists(dest); i++)
                    dest = Path.Combine(destDir, $"{Path.GetFileNameWithoutExtension(file)} ({i}){Path.GetExtension(file)}");
                File.Move(file, dest);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

            if (track is not null) { track.FilePath = dest; track.FileName = Path.GetFileName(dest); }
            moved++;
        }

        var removed = RemoveEmptyFolders(libraryDir);
        return new OrganizeResult(moved, removed);
    }

    /// <summary>Deletes every folder under (but not including) <paramref name="root"/> left with nothing in it, deepest first.</summary>
    private static int RemoveEmptyFolders(string root)
    {
        var removed = 0;
        var dirs = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .OrderByDescending(d => d.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar));
        foreach (var dir in dirs)
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                    removed++;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return removed;
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
