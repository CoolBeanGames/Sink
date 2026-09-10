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

        // A YouTube Music artist handle (music.youtube.com/@name) resolves to a
        // plain YouTube channel — its "Videos" / "Shorts" tabs, not the music
        // discography — so it looks like a tiny "album". When the link is an
        // artist/channel (or the scan came back as nothing but channel tabs),
        // retry against the channel's Releases / Playlists tabs, which do list
        // the albums.
        var looksLikeArtist = ArtistUrl().IsMatch(url)
            || (info.IsPlaylist && info.TrackTitles is { Count: > 0 } && info.TrackTitles.All(IsChannelTabName));
        if (!looksLikeArtist) return info;

        foreach (var candidate in await ArtistDiscographyUrlsAsync(url, token).ConfigureAwait(false))
        {
            if (string.Equals(candidate, url, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var retry = await ScanRawAsync(candidate, token).ConfigureAwait(false);
                if (retry.Albums is { Count: > 0 })
                    return retry;
            }
            catch (Exception e) when (e is InvalidOperationException) { }
        }
        return info;
    }

    /// <summary>Candidate URLs that list an artist's albums, best first.</summary>
    private static async Task<IReadOnlyList<string>> ArtistDiscographyUrlsAsync(string url, CancellationToken token)
    {
        var urls = new List<string>();
        var handle = HandleName().Match(url);
        if (handle.Success)
        {
            var h = handle.Groups[1].Value;
            urls.Add($"https://www.youtube.com/@{h}/releases");
            urls.Add($"https://www.youtube.com/@{h}/playlists");
        }

        // A bare /channel/UC… link, or a handle whose Releases tab was empty:
        // fall back to the resolved channel id.
        var id = ChannelId().Match(url) is { Success: true } cm
            ? cm.Groups[1].Value
            : await ResolveChannelIdAsync(url, token).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(id))
        {
            urls.Add($"https://www.youtube.com/channel/{id}/releases");
            urls.Add($"https://www.youtube.com/channel/{id}/playlists");
            urls.Add($"https://music.youtube.com/channel/{id}");
        }
        return urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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

    /// <summary>Temp folder for double-click preview downloads; wiped when previews end.</summary>
    public static string PreviewDirectory { get; } = Path.Combine(Path.GetTempPath(), "SinkPreview");

    /// <summary>
    /// Downloads one Album or Single <see cref="DownloadNode"/> to one or more
    /// audio files, honouring <paramref name="options"/> and the per-track
    /// include toggles, and reports 0..1 progress. Returns every finished file.
    /// </summary>
    public static async Task<IReadOnlyList<string>> DownloadAsync(
        DownloadNode node, DownloadOptions options, IProgress<double> progress,
        IProgress<string>? status = null, CancellationToken token = default)
    {
        System.IO.Directory.CreateDirectory(DownloadsDirectory);
        var workDir = Path.Combine(DownloadsDirectory, "_" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(workDir);

        var isPlaylist = node.Kind == DownloadKind.Album;
        var artist = FirstReal(node.Artist, node.Parent?.Artist) ?? "Unknown Artist";
        var album = node.Album;
        var genre = FirstReal(node.Genre, node.Parent?.Genre) ?? "Unknown";

        // A partial album — some tracks toggled off — downloads just the chosen
        // playlist positions; otherwise the whole link comes down.
        var trackNodes = node.Children.Where(c => c.Kind == DownloadKind.Track).ToList();
        var chosen = trackNodes.Where(t => t.Enabled == true).OrderBy(t => t.Index).ToList();
        var partial = isPlaylist && trackNodes.Count > 0 && chosen.Count < trackNodes.Count;
        var titleOrder = isPlaylist && trackNodes.Count > 0
            ? (partial ? chosen : trackNodes.OrderBy(t => t.Index).ToList())
            : new List<DownloadNode>();

        var total = Math.Max(1, isPlaylist ? (partial ? chosen.Count : Math.Max(trackNodes.Count, 1)) : 1);
        var args = new List<string>
        {
            "-x",
            "--audio-format", options.FormatExtension,
            "--audio-quality", options.Quality > 0 ? options.Quality + "K" : "0",
            isPlaylist ? "--yes-playlist" : "--no-playlist",
            "--no-warnings",
            "--newline",
            "--no-overwrites",
            "--retries", "5",
            "--fragment-retries", "5",
            "--ffmpeg-location", ToolManager.Directory,
            "-o", Path.Combine(workDir, isPlaylist ? "%(playlist_index)03d - %(title)s.%(ext)s" : "%(title)s.%(ext)s"),
        };
        // A user-supplied cover replaces whatever yt-dlp would embed.
        var artOverride = node.ArtworkOverride ?? node.Parent?.ArtworkOverride;
        var artBytes = string.IsNullOrWhiteSpace(artOverride) ? null : Artwork.SquareCropBytes(artOverride);

        if (partial) { args.Add("--playlist-items"); args.Add(string.Join(",", chosen.Select(t => t.Index))); }
        if (options.WriteMetadata) args.Add("--embed-metadata");
        if (options.EmbedAlbumArt && artBytes is null) { args.Add("--embed-thumbnail"); args.Add("--convert-thumbnails"); args.Add("jpg"); }
        args.Add(node.Url);

        var current = 0;
        var source = isPlaylist ? album : node.Title;
        status?.Report(isPlaylist ? $"Downloading track 1/{total} from {source}" : $"Downloading {source}");
        // Light up each track as yt-dlp reaches it (task 114). yt-dlp works
        // through an album in list order, so when it announces "item n" the
        // ones before it are done and this one is now downloading.
        void MarkTrackProgress(int itemIndex1Based)
        {
            if (!isPlaylist || titleOrder.Count == 0) return;
            for (var i = 0; i < titleOrder.Count && i < itemIndex1Based - 1; i++)
                if (titleOrder[i].State is DownloadState.Downloading or DownloadState.Pending or DownloadState.Ready)
                    titleOrder[i].State = DownloadState.Done;
            if (itemIndex1Based - 1 < titleOrder.Count)
            {
                var node2 = titleOrder[itemIndex1Based - 1];
                node2.State = DownloadState.Downloading;
                node2.StatusText = "Downloading…";
            }
        }

        if (isPlaylist && titleOrder.Count > 0) MarkTrackProgress(1);
        var (exit, _, stderr) = await RunAsync(args, line =>
        {
            var itemMatch = PlaylistItemLine().Match(line);
            if (itemMatch.Success && int.TryParse(itemMatch.Groups[1].Value, out var n))
            {
                current = n - 1;
                MarkTrackProgress(n);
                status?.Report(isPlaylist ? $"Downloading track {n}/{total} from {source}" : $"Downloading {source}");
            }

            var pctMatch = ProgressLine().Match(line);
            if (pctMatch.Success && double.TryParse(pctMatch.Groups[1].Value, out var pct))
            {
                progress.Report(Math.Clamp((current + pct / 100.0) / total, 0, 1));
                if (isPlaylist && current >= 0 && current < titleOrder.Count)
                    titleOrder[current].Progress = pct / 100.0;
            }
        }, token).ConfigureAwait(false);

        var produced = System.IO.Directory.EnumerateFiles(workDir)
            .Where(f => AudioExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // yt-dlp exits non-zero if *any* track failed. Keep whatever it did
        // manage to fetch and mark the rest failed, so the caller can leave the
        // failed tracks in the tree for a retry.
        if (produced.Count == 0)
        {
            var reason = FirstError(stderr) ?? "yt-dlp produced no audio";
            foreach (var t in trackNodes) { t.State = DownloadState.Failed; t.StatusText = reason; }
            Log.Error($"Download failed for {node.Name} ({node.Url}): {reason}");
            throw new InvalidOperationException(reason);
        }

        // Map produced files to playlist positions ("001 - Title.mp3").
        var byIndex = new Dictionary<int, string>();
        foreach (var f in produced)
        {
            var m = ProducedIndex().Match(Path.GetFileName(f));
            if (m.Success && int.TryParse(m.Groups[1].Value, out var idx)) byIndex[idx] = f;
        }

        var finished = new List<string>();
        var ordered = isPlaylist
            ? titleOrder
            : new List<DownloadNode> { node };
        for (var i = 0; i < ordered.Count; i++)
        {
            var trackNode = ordered[i];
            string? file = isPlaylist
                ? (byIndex.TryGetValue(trackNode.Index, out var byIdx) ? byIdx : (i < produced.Count && byIndex.Count == 0 ? produced[i] : null))
                : produced[0];
            if (file is null)
            {
                if (trackNode.Kind == DownloadKind.Track)
                {
                    trackNode.State = DownloadState.Failed;
                    trackNode.StatusText = FirstError(stderr) ?? "yt-dlp skipped this track";
                    Log.Warn($"Track {trackNode.Index} \"{trackNode.Name}\" of {album} did not download: {trackNode.StatusText}");
                }
                continue;
            }

            var stem = isPlaylist ? Path.GetFileNameWithoutExtension(file) : $"{artist} - {node.Title}";
            var finalPath = UniquePath(Path.Combine(DownloadsDirectory, Sanitize(stem) + Path.GetExtension(file)));
            File.Move(file, finalPath, overwrite: false);

            // Write an edited title: always for a single, and per-track for an
            // album when the user renamed that track. An untouched album track
            // keeps yt-dlp's own title (see archived task 49).
            var title = !isPlaylist ? node.Title
                : (trackNode.TitleEdited ? trackNode.Title.Trim() : null);
            var trackNo = trackNode.TrackNumber > 0 ? trackNode.TrackNumber
                : (options.NumberTracks && isPlaylist && trackNode.Index > 0 ? trackNode.Index : 0);
            ApplyTags(finalPath, artist, album, genre, title, trackNo, artBytes);
            if (trackNode.Kind == DownloadKind.Track) trackNode.State = DownloadState.Done;
            finished.Add(finalPath);
        }
        try { System.IO.Directory.Delete(workDir, recursive: true); } catch (IOException) { }

        progress.Report(1);
        if (finished.Count == 0)
        {
            var reason = FirstError(stderr) ?? "Download failed";
            Log.Error($"Download failed for {node.Name} ({node.Url}): {reason}");
            throw new InvalidOperationException(reason);
        }
        if (exit != 0 && isPlaylist && finished.Count < titleOrder.Count)
        {
            var reason = FirstError(stderr) ?? "yt-dlp reported an error on one or more tracks";
            Log.Warn($"Partial album download for {album} ({node.Url}): {finished.Count}/{titleOrder.Count} tracks — {reason}");
            throw new PartialDownloadException(finished, reason);
        }
        return finished;
    }

    /// <summary>Thrown when an album partly downloaded — carries the files that did land and why the rest didn't.</summary>
    public sealed class PartialDownloadException(IReadOnlyList<string> downloaded, string reason)
        : Exception(reason)
    {
        public IReadOnlyList<string> Downloaded { get; } = downloaded;
        public string Reason { get; } = reason;
    }

    [GeneratedRegex(@"^(\d+)\s*-\s*")]
    private static partial Regex ProducedIndex();

    /// <summary>
    /// Downloads a single track to a temp file for double-click preview. No tags,
    /// no import. <paramref name="playlistIndex"/> &gt; 0 pulls that one position
    /// out of the album at <paramref name="sourceUrl"/>; 0 treats the URL as a
    /// standalone video.
    /// </summary>
    public static async Task<string> PreviewTrackAsync(
        string sourceUrl, int playlistIndex, DownloadOptions options,
        IProgress<string>? status = null, CancellationToken token = default)
    {
        System.IO.Directory.CreateDirectory(PreviewDirectory);
        var workDir = Path.Combine(PreviewDirectory, Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(workDir);

        var args = new List<string>
        {
            "-x",
            "--audio-format", options.FormatExtension,
            "--audio-quality", "5",
            "--no-warnings", "--newline", "--no-overwrites",
            "--embed-thumbnail", "--convert-thumbnails", "jpg",
            "--ffmpeg-location", ToolManager.Directory,
            "-o", Path.Combine(workDir, "%(title)s.%(ext)s"),
        };
        if (playlistIndex > 0) { args.Add("--yes-playlist"); args.Add("--playlist-items"); args.Add(playlistIndex.ToString()); }
        else args.Add("--no-playlist");
        args.Add(sourceUrl);

        status?.Report("Fetching preview…");
        var (exit, _, stderr) = await RunAsync(args, _ => { }, token).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException(FirstError(stderr) ?? "Preview download failed");

        return System.IO.Directory.EnumerateFiles(workDir)
                   .FirstOrDefault(f => AudioExtensions.Contains(Path.GetExtension(f)))
               ?? throw new InvalidOperationException("yt-dlp produced no audio file");
    }

    /// <summary>Deletes every preview temp file. Safe to call any time.</summary>
    public static void ClearPreviews()
    {
        try { if (System.IO.Directory.Exists(PreviewDirectory)) System.IO.Directory.Delete(PreviewDirectory, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static string? FirstReal(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v) && v != "Unknown Artist" && v != "Unknown");

    /// <summary>Writes the user's (possibly edited) metadata over whatever yt-dlp embedded.</summary>
    private static void ApplyTags(
        string path, string artist, string album, string genre, string? title, int trackNo, byte[]? artwork)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            if (!string.IsNullOrWhiteSpace(title)) file.Tag.Title = title;
            if (!string.IsNullOrWhiteSpace(artist) && artist != "Unknown Artist")
            {
                file.Tag.Performers = [artist];
                file.Tag.AlbumArtists = [artist];
            }
            if (!string.IsNullOrWhiteSpace(album)) file.Tag.Album = album;
            if (!string.IsNullOrWhiteSpace(genre) && genre != "Unknown")
                file.Tag.Genres = [genre];
            if (trackNo > 0) file.Tag.Track = (uint)trackNo;
            if (artwork is { Length: > 0 })
                file.Tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector(artwork))
                {
                    Type = TagLib.PictureType.FrontCover,
                    MimeType = "image/jpeg",
                    Description = "Cover",
                }];
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

    /// <summary>Asks yt-dlp for a channel/handle URL's canonical UC… channel id (no entries resolved).</summary>
    private static async Task<string?> ResolveChannelIdAsync(string url, CancellationToken token)
    {
        try
        {
            var (exit, stdout, _) = await RunAsync(
                ["-J", "--flat-playlist", "--playlist-items", "0", "--no-warnings", url], null, token).ConfigureAwait(false);
            if (exit != 0) return null;
            using var doc = JsonDocument.Parse(stdout);
            var r = doc.RootElement;
            var id = Str(r, "channel_id") ?? Str(r, "uploader_id")
                     ?? (Str(r, "id") is { } s && s.StartsWith("UC", StringComparison.Ordinal) ? s : null);
            return string.IsNullOrWhiteSpace(id) ? null : id;
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

    [GeneratedRegex(@"/@([A-Za-z0-9._-]+)")]
    private static partial Regex HandleName();

    [GeneratedRegex(@"/channel/(UC[A-Za-z0-9_-]+)")]
    private static partial Regex ChannelId();
}
