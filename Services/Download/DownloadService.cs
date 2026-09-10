using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sink.Services.Download;

public sealed record ScannedInfo(
    string Title, string Artist, string Album, string Genre, bool IsPlaylist, int TrackCount,
    IReadOnlyList<ScannedAlbum>? Albums = null, IReadOnlyList<string>? TrackTitles = null);

/// <summary>One album found on a YouTube Music artist page.</summary>
public sealed record ScannedAlbum(string Url, string Title);

/// <summary>
/// Drives yt-dlp: a metadata-only scan of a link, and the actual audio
/// download. Both shell out to the local yt-dlp/ffmpeg managed by
/// <see cref="ToolManager"/>.
/// </summary>
public static partial class DownloadService
{
    /// <summary>Finished tracks land in the user's library folder (Settings).</summary>
    public static string DownloadsDirectory =>
        string.IsNullOrWhiteSpace(AppSettings.Current.LibraryLocation)
            ? Path.Combine(LibraryStore.Directory, "downloads")
            : AppSettings.Current.LibraryLocation;

    /// <summary>
    /// Fetches title/artist/album/genre for a link without downloading media.
    /// A playlist / album link is reported as one <see cref="ScannedInfo"/> with
    /// <see cref="ScannedInfo.IsPlaylist"/> set; a watch URL that carries a
    /// &amp;list= is still treated as a single track (yt-dlp's default).
    /// </summary>
    public static async Task<ScannedInfo> ScanAsync(string url, CancellationToken token = default)
    {
        var info = await ScanRawAsync(url, token).ConfigureAwait(false);
        if (info.Albums is { Count: > 0 }) return info;

        // A YouTube Music artist handle (music.youtube.com/@name) resolves to the
        // artist's *Videos* tab by default, so it looks like a 39-track "album".
        // Re-scan through the canonical channel URL, which lists the discography.
        if (ArtistUrl().IsMatch(url) && info.IsPlaylist)
        {
            var channelUrl = await ResolveChannelUrlAsync(url, token).ConfigureAwait(false);
            if (channelUrl is not null && !string.Equals(channelUrl, url, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var retry = await ScanRawAsync(channelUrl, token).ConfigureAwait(false);
                    if (retry.Albums is { Count: > 0 }) return retry;
                }
                catch (Exception e) when (e is InvalidOperationException) { }
            }
        }
        return info;
    }

    private static async Task<ScannedInfo> ScanRawAsync(string url, CancellationToken token)
    {
        // No --no-playlist: let yt-dlp decide. --flat-playlist keeps it fast by
        // not resolving every entry of an album.
        var (exit, stdout, stderr) = await RunAsync(
            ["-J", "--flat-playlist", "--no-warnings", url], null, token).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException(FirstError(stderr) ?? "yt-dlp could not read that link");

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;

        if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array && entries.GetArrayLength() > 0)
        {
            var entryList = entries.EnumerateArray().ToList();

            // A YouTube Music artist / channel page flattens to a list whose
            // entries are themselves playlists (albums), not videos. Hand those
            // album links back so the caller can scan each one on its own.
            var albums = entryList
                .Where(en => IsPlaylistEntry(en))
                .Select(en => new ScannedAlbum(Str(en, "url") ?? "", StripCollectionPrefix(Str(en, "title") ?? "Album")))
                .Where(a => a.Url.Length > 0 && !IsChannelTabName(a.Title))
                .ToList();
            // Treat it as an artist page when (almost) every entry is a playlist.
            if (albums.Count > 0 && albums.Count >= entryList.Count - 3)
            {
                var who = CleanUploader(Str(root, "uploader") ?? Str(root, "channel") ?? Str(root, "title")) ?? "Unknown Artist";
                return new ScannedInfo(who.Trim(), who.Trim(), "", "Unknown", IsPlaylist: false, TrackCount: 0, Albums: albums);
            }

            var count = entryList.Count;
            var album = StripCollectionPrefix(Str(root, "title") ?? Str(root, "playlist_title") ?? "Unknown Album");
            var artist = CleanUploader(Str(root, "uploader") ?? Str(root, "channel") ?? Str(entries[0], "uploader") ?? Str(entries[0], "channel")) ?? "Unknown Artist";
            var titles = entryList.Select(en => (Str(en, "track") ?? Str(en, "title") ?? "Untitled").Trim()).ToList();
            return new ScannedInfo(album.Trim(), artist.Trim(), album.Trim(), "Unknown", IsPlaylist: true, TrackCount: count, TrackTitles: titles);
        }

        var title = Str(root, "track") ?? Str(root, "title") ?? "Unknown title";
        var single = Str(root, "artist") ?? Str(root, "creator") ?? CleanUploader(Str(root, "uploader") ?? Str(root, "channel")) ?? "Unknown Artist";
        var singleAlbum = StripCollectionPrefix(Str(root, "album") ?? Str(root, "playlist_title") ?? Str(root, "playlist") ?? title);
        var genre = Str(root, "genre") ?? "Unknown";
        return new ScannedInfo(title.Trim(), single.Trim(), singleAlbum.Trim(), genre.Trim(), IsPlaylist: false, TrackCount: 1);
    }

    /// <summary>
    /// Downloads a link to one or more audio files, honouring <paramref name="options"/>,
    /// and reports 0..1 progress across the whole link. Returns every finished file
    /// (one for a single track, many for an album / playlist).
    /// </summary>
    public static async Task<IReadOnlyList<string>> DownloadAsync(
        DownloadItem item, DownloadOptions options, IProgress<double> progress,
        IProgress<string>? status = null, CancellationToken token = default)
    {
        System.IO.Directory.CreateDirectory(DownloadsDirectory);
        var workDir = Path.Combine(DownloadsDirectory, "_" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(workDir);

        // A partial album — some tracks toggled off — downloads just the chosen
        // playlist positions; otherwise the whole link comes down as before.
        var chosen = item.Tracks.Where(t => t.Enabled).OrderBy(t => t.Index).ToList();
        var partial = item.IsPlaylist && item.Tracks.Count > 0 && chosen.Count < item.Tracks.Count;
        var titleOrder = item.IsPlaylist && item.Tracks.Count > 0
            ? (partial ? chosen : item.Tracks.OrderBy(t => t.Index).ToList())
            : new List<TrackChoice>();

        var total = Math.Max(1, item.IsPlaylist ? (partial ? chosen.Count : Math.Max(item.TrackCount, item.Tracks.Count)) : 1);
        var args = new List<string>
        {
            "-x",
            "--audio-format", options.FormatExtension,
            "--audio-quality", options.Quality > 0 ? options.Quality + "K" : "0",
            item.IsPlaylist ? "--yes-playlist" : "--no-playlist",
            "--no-warnings",
            "--newline",
            "--no-overwrites",
            "--retries", "5",
            "--fragment-retries", "5",
            "--ffmpeg-location", ToolManager.Directory,
            "-o", Path.Combine(workDir, item.IsPlaylist ? "%(playlist_index)03d - %(title)s.%(ext)s" : "%(title)s.%(ext)s"),
        };
        if (partial) { args.Add("--playlist-items"); args.Add(string.Join(",", chosen.Select(t => t.Index))); }
        if (options.WriteMetadata) args.Add("--embed-metadata");
        if (options.EmbedAlbumArt) { args.Add("--embed-thumbnail"); args.Add("--convert-thumbnails"); args.Add("jpg"); }
        args.Add(item.Url);

        var current = 0;
        var source = item.IsPlaylist ? item.Album : item.Title;
        status?.Report(item.IsPlaylist ? $"Downloading track 1/{total} from {source}" : $"Downloading {source}");
        var (exit, _, stderr) = await RunAsync(args, line =>
        {
            var itemMatch = PlaylistItemLine().Match(line);
            if (itemMatch.Success && int.TryParse(itemMatch.Groups[1].Value, out var n))
            {
                current = n - 1;
                status?.Report(item.IsPlaylist
                    ? $"Downloading track {n}/{total} from {source}"
                    : $"Downloading {source}");
            }

            var pctMatch = ProgressLine().Match(line);
            if (pctMatch.Success && double.TryParse(pctMatch.Groups[1].Value, out var pct))
                progress.Report(Math.Clamp((current + pct / 100.0) / total, 0, 1));
        }, token).ConfigureAwait(false);

        if (exit != 0)
            throw new InvalidOperationException(FirstError(stderr) ?? "Download failed");

        var produced = System.IO.Directory.EnumerateFiles(workDir)
            .Where(f => AudioExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (produced.Count == 0)
            throw new InvalidOperationException("yt-dlp produced no audio file");

        var finished = new List<string>();
        for (var i = 0; i < produced.Count; i++)
        {
            var file = produced[i];
            var stem = item.IsPlaylist
                ? Path.GetFileNameWithoutExtension(file)
                : $"{item.Artist} - {item.Title}";
            var finalPath = UniquePath(Path.Combine(DownloadsDirectory, Sanitize(stem) + Path.GetExtension(file)));
            File.Move(file, finalPath, overwrite: false);

            // Write an edited title: always for a single, and per-track for an
            // album when the user renamed that track. An untouched album track
            // keeps yt-dlp's own title (see archived task 49).
            var title = !item.IsPlaylist ? item.Title
                : (i < titleOrder.Count && titleOrder[i].TitleEdited ? titleOrder[i].Title.Trim() : null);
            ApplyTags(finalPath, item, title);
            finished.Add(finalPath);
        }
        try { System.IO.Directory.Delete(workDir, recursive: true); } catch (IOException) { }

        progress.Report(1);
        return finished;
    }

    /// <summary>
    /// Writes the user's (possibly edited) album-level metadata over whatever
    /// yt-dlp embedded. Title is deliberately left alone — one row can stand for
    /// a whole album, so each track keeps its own downloaded title.
    /// </summary>
    private static void ApplyTags(string path, DownloadItem item, string? title = null)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            if (!string.IsNullOrWhiteSpace(title)) file.Tag.Title = title;
            if (!string.IsNullOrWhiteSpace(item.Artist) && item.Artist != "Unknown Artist")
            {
                file.Tag.Performers = [item.Artist];
                file.Tag.AlbumArtists = [item.Artist];
            }
            if (!string.IsNullOrWhiteSpace(item.Album)) file.Tag.Album = item.Album;
            if (!string.IsNullOrWhiteSpace(item.Genre) && item.Genre != "Unknown")
                file.Tag.Genres = [item.Genre];
            file.Save();
        }
        catch (Exception e) when (e is not OutOfMemoryException) { }
    }

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".aac", ".opus", ".ogg", ".flac", ".wav"
    };

    private static bool IsChannelTabName(string title) => title.Trim() is
        "Videos" or "Shorts" or "Live" or "Podcasts" or "Playlists" or "Releases" or "Community" or "Home" or "Store";

    /// <summary>
    /// Asks yt-dlp for a channel/handle URL's canonical channel id (no entries
    /// resolved) and returns the YouTube Music artist URL for it.
    /// </summary>
    private static async Task<string?> ResolveChannelUrlAsync(string url, CancellationToken token)
    {
        try
        {
            var (exit, stdout, _) = await RunAsync(
                ["-J", "--flat-playlist", "--playlist-items", "0", "--no-warnings", url], null, token).ConfigureAwait(false);
            if (exit != 0) return null;
            using var doc = JsonDocument.Parse(stdout);
            var id = Str(doc.RootElement, "channel_id") ?? Str(doc.RootElement, "uploader_id") ?? Str(doc.RootElement, "id");
            if (string.IsNullOrWhiteSpace(id)) return null;
            return id.StartsWith("UC", StringComparison.Ordinal)
                ? $"https://music.youtube.com/channel/{id}"
                : $"https://music.youtube.com/{id}";
        }
        catch (Exception e) when (e is InvalidOperationException or JsonException)
        {
            return null;
        }
    }

    /// <summary>True when a flat-playlist entry points at another playlist / album rather than a single video.</summary>
    private static bool IsPlaylistEntry(JsonElement entry)
    {
        var url = Str(entry, "url") ?? "";
        if (url.Contains("watch", StringComparison.OrdinalIgnoreCase) || url.Contains("/watch?", StringComparison.OrdinalIgnoreCase))
            return false;
        if (url.Contains("playlist?", StringComparison.OrdinalIgnoreCase) || url.Contains("/browse/", StringComparison.OrdinalIgnoreCase))
            return true;
        var kind = Str(entry, "_type");
        if (string.Equals(kind, "playlist", StringComparison.OrdinalIgnoreCase)) return true;
        var ieKey = Str(entry, "ie_key") ?? "";
        return ieKey.Contains("Tab", StringComparison.OrdinalIgnoreCase) || ieKey.Contains("Playlist", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    /// <summary>
    /// YouTube Music names an album's auto-playlist "Album - <name>" (likewise
    /// "Single - ", "EP - ", "Playlist - "). Drop that leading label so the grid
    /// shows just the album name.
    /// </summary>
    private static string StripCollectionPrefix(string value) =>
        CollectionPrefix().Replace(value, "").Trim() is { Length: > 0 } s ? s : value.Trim();

    private static string? CleanUploader(string? uploader) =>
        string.IsNullOrWhiteSpace(uploader) ? null : uploader.Replace(" - Topic", "", StringComparison.OrdinalIgnoreCase).Trim();

    private static string? FirstError(string stderr) => stderr
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .FirstOrDefault(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
        ?.Replace("ERROR:", "").Trim();

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length > 120 ? name[..120] : name;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static async Task<(int exit, string stdout, string stderr)> RunAsync(
        IReadOnlyList<string> arguments, Action<string>? onLine, CancellationToken token)
    {
        if (!File.Exists(ToolManager.YtDlpPath))
            throw new InvalidOperationException("yt-dlp is not installed yet");

        var psi = new ProcessStartInfo(ToolManager.YtDlpPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        // --newline makes yt-dlp emit progress as discrete stdout lines; feed
        // every stdout line to the caller so progress updates arrive live.
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdout.AppendLine(e.Data);
            onLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    [GeneratedRegex(@"\[download\]\s+([0-9.]+)%")]
    private static partial Regex ProgressLine();

    [GeneratedRegex(@"Downloading item (\d+) of (\d+)")]
    private static partial Regex PlaylistItemLine();

    [GeneratedRegex(@"^\s*(album|single|ep|playlist)\s*[-–—]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex CollectionPrefix();

    [GeneratedRegex(@"(youtube\.com|music\.youtube\.com)/(@|channel/|c/|user/|artist/)", RegexOptions.IgnoreCase)]
    private static partial Regex ArtistUrl();
}
