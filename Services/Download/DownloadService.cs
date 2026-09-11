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
    /// Each track is moved into <see cref="DownloadsDirectory"/> and tagged (and,
    /// if given, reported through <paramref name="onTrackFile"/>) as soon as
    /// yt-dlp finishes it — not batched until the whole album completes — so a
    /// caller doing copy/move-on-import can relocate each track as it lands
    /// instead of flushing the whole album at the end (task 121).
    /// </summary>
    public static async Task<IReadOnlyList<string>> DownloadAsync(
        DownloadNode node, DownloadOptions options, IProgress<double> progress,
        IProgress<string>? status = null, IProgress<string>? onTrackFile = null, CancellationToken token = default)
    {
        System.IO.Directory.CreateDirectory(DownloadsDirectory);
        var workDir = Path.Combine(DownloadsDirectory, "_" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(workDir);

        // yt-dlp's Process.OutputDataReceived (and anything after the
        // ConfigureAwait(false) below) fires on a thread-pool thread, not the
        // UI thread — but MarkTrackProgress/FinalizeTrack mutate WPF-bound
        // DownloadNode properties. Left undispatched, a failed download's
        // foreach over every track threw repeatedly off-thread, which .NET
        // treats as fatal and took the whole app down instead of just
        // surfacing an error (task 145).
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        void OnUi(Action action)
        {
            if (dispatcher is null || dispatcher.CheckAccess()) action();
            else dispatcher.Invoke(action);
        }

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
        var finished = new List<string>();
        var finalized = new HashSet<int>(); // playlist index already moved+tagged (single always uses -1)

        // Moves one track's produced file out of workDir, tags it, and reports
        // it — called the moment yt-dlp finishes that track (mid-run, from the
        // line callback below) rather than waiting for the whole album, so a
        // copy/move importer can relocate it right away (task 121). Safe to
        // call more than once for the same track; a miss can be retried once
        // the process has exited and the file is definitely there (or not).
        bool FinalizeTrack(DownloadNode trackNode)
        {
            var key = isPlaylist ? trackNode.Index : -1;
            if (finalized.Contains(key)) return true;

            string? file;
            if (isPlaylist)
                file = System.IO.Directory.EnumerateFiles(workDir)
                    .Where(f => AudioExtensions.Contains(Path.GetExtension(f)))
                    .FirstOrDefault(f => ProducedIndex().Match(Path.GetFileName(f)) is { Success: true } m
                                         && int.TryParse(m.Groups[1].Value, out var idx) && idx == trackNode.Index);
            else
                file = System.IO.Directory.EnumerateFiles(workDir)
                    .FirstOrDefault(f => AudioExtensions.Contains(Path.GetExtension(f)));
            if (file is null) return false;

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

            if (trackNode.Kind == DownloadKind.Track) OnUi(() => trackNode.State = DownloadState.Done);
            finalized.Add(key);
            finished.Add(finalPath);
            onTrackFile?.Report(finalPath);
            return true;
        }

        // Light up (and finalize) each track as yt-dlp reaches it (task 114,
        // 121). yt-dlp works through an album in list order, so when it
        // announces "item n" the ones before it are done and this one is now
        // downloading.
        void MarkTrackProgress(int itemIndex1Based)
        {
            if (!isPlaylist || titleOrder.Count == 0) return;
            for (var i = 0; i < titleOrder.Count && i < itemIndex1Based - 1; i++)
            {
                var t = titleOrder[i];
                if (t.State is DownloadState.Done or DownloadState.Failed) continue;
                if (!FinalizeTrack(t))
                    OnUi(() => { t.State = DownloadState.Failed; t.StatusText = "yt-dlp skipped this track"; });
            }
            if (itemIndex1Based - 1 < titleOrder.Count)
            {
                var next = titleOrder[itemIndex1Based - 1];
                OnUi(() => { next.State = DownloadState.Downloading; next.StatusText = "Downloading…"; });
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
                {
                    var track = titleOrder[current];
                    OnUi(() => track.Progress = pct / 100.0);
                }
            }
        }, token).ConfigureAwait(false);

        // Whatever wasn't finalized mid-run (the last track — there's no "next
        // item" line to trigger it — or one that failed) gets a final pass now
        // that yt-dlp has exited and every file it's going to produce exists.
        var ordered = isPlaylist ? titleOrder : new List<DownloadNode> { node };
        foreach (var trackNode in ordered)
        {
            var key = isPlaylist ? trackNode.Index : -1;
            if (finalized.Contains(key)) continue;
            if (!FinalizeTrack(trackNode) && trackNode.Kind == DownloadKind.Track)
            {
                var failureText = FirstError(stderr) ?? "yt-dlp skipped this track";
                OnUi(() => { trackNode.State = DownloadState.Failed; trackNode.StatusText = failureText; });
                Log.Warn($"Track {trackNode.Index} \"{trackNode.Name}\" of {album} did not download: {failureText}");
            }
        }
        progress.Report(1);
        if (finished.Count == 0)
        {
            var reason = FirstError(stderr) ?? "yt-dlp produced no audio";
            OnUi(() => { foreach (var t in trackNodes) { t.State = DownloadState.Failed; t.StatusText = reason; } });
            // No line in stderr started with "ERROR", yet nothing came out — log
            // everything we have (exit code, leftover files, full stderr) since
            // the short "reason" alone hasn't been enough to explain this before.
            var leftover = string.Join(", ", SafeListFiles(workDir));
            Log.Error($"Download failed for {node.Name} ({node.Url}): {reason} — exit {exit}, workDir files: [{leftover}]\nstderr:\n{stderr}");
            try { System.IO.Directory.Delete(workDir, recursive: true); } catch (IOException) { }
            throw new InvalidOperationException(reason);
        }
        try { System.IO.Directory.Delete(workDir, recursive: true); } catch (IOException) { }
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

    private static IReadOnlyList<string> SafeListFiles(string dir)
    {
        try { return System.IO.Directory.EnumerateFiles(dir).Select(Path.GetFileName).Where(n => n is not null).Select(n => n!).ToList(); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static string? FirstError(string stderr)
    {
        var raw = stderr
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
            ?.Replace("ERROR:", "").Trim();
        if (raw is null) return null;

        // yt-dlp's generic bot-check message is meaningless to a user who has
        // never heard of it; point them straight at the fix (task 123).
        if (raw.Contains("not a bot", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase))
            return "YouTube is asking Sink to sign in. Open Settings and set \"YouTube cookies\" to the browser you're signed into YouTube with, then try again.";
        if (raw.Contains("dpapi", StringComparison.OrdinalIgnoreCase))
            return "Sink couldn't read your browser's saved cookies (Windows DPAPI decryption failed). Try picking a different browser under Settings → \"YouTube cookies\", or set it to \"none\".";
        return raw;
    }

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

    /// <summary>
    /// yt-dlp args that hand it the user's browser cookies, so a request looks
    /// like it came from a signed-in browser instead of a bare script — the fix
    /// for YouTube's "Sign in to confirm you're not a bot" wall (task 123).
    /// "auto" (the default) picks the first browser it finds installed; "none"
    /// disables this entirely.
    /// </summary>
    private static IReadOnlyList<string> CookieArgs()
    {
        if (_cookiesKnownBroken) return [];
        var choice = (AppSettings.Current.YouTubeCookies ?? "auto").Trim().ToLowerInvariant();
        if (choice is "none" or "off" or "") return [];
        var browser = choice == "auto" ? DetectInstalledBrowser() : choice;
        return browser is null ? [] : ["--cookies-from-browser", browser];
    }

    /// <summary>
    /// Set once cookie extraction fails in a way that won't fix itself mid-session
    /// (DPAPI can't decrypt Chrome/Edge's App-Bound-encrypted cookie store —
    /// yt-dlp#10927). Skips the cookie attempt on every later call instead of
    /// paying for — and failing — it again on every single scan and download.
    /// </summary>
    private static bool _cookiesKnownBroken;

    /// <summary>The yt-dlp browser name for the first browser profile folder that exists, or null.</summary>
    private static string? DetectInstalledBrowser()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        (string Name, string Path)[] candidates =
        [
            ("edge", Path.Combine(local, "Microsoft", "Edge", "User Data")),
            ("chrome", Path.Combine(local, "Google", "Chrome", "User Data")),
            ("brave", Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data")),
            ("firefox", Path.Combine(roaming, "Mozilla", "Firefox", "Profiles")),
            ("vivaldi", Path.Combine(local, "Vivaldi", "User Data")),
            ("opera", Path.Combine(roaming, "Opera Software", "Opera Stable")),
        ];
        foreach (var c in candidates)
            if (System.IO.Directory.Exists(c.Path)) return c.Name;
        return null;
    }

    private static bool LooksLikeCookieProblem(string stderr) =>
        // "Failed to decrypt with DPAPI" (yt-dlp#10927) never mentions the word
        // "cookie" at all, so it needs its own check alongside the general one.
        stderr.Contains("dpapi", StringComparison.OrdinalIgnoreCase)
        || (stderr.Contains("cookie", StringComparison.OrdinalIgnoreCase) &&
        (stderr.Contains("could not", StringComparison.OrdinalIgnoreCase)
         || stderr.Contains("unable to", StringComparison.OrdinalIgnoreCase)
         || stderr.Contains("permission", StringComparison.OrdinalIgnoreCase)
         || stderr.Contains("decrypt", StringComparison.OrdinalIgnoreCase)
         || stderr.Contains("database", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Runs yt-dlp with browser cookies attached when configured, and falls back
    /// to a cookie-less run automatically if reading them failed (a locked
    /// profile, no matching browser, etc.) rather than breaking downloads that
    /// worked fine before.
    /// </summary>
    private static async Task<(int exit, string stdout, string stderr)> RunAsync(
        IReadOnlyList<string> arguments, Action<string>? onLine, CancellationToken token)
    {
        var cookieArgs = CookieArgs();
        if (cookieArgs.Count == 0)
            return await RunProcessAsync(arguments, onLine, token).ConfigureAwait(false);

        var result = await RunProcessAsync(cookieArgs.Concat(arguments).ToList(), onLine, token).ConfigureAwait(false);
        if (result.exit == 0 || !LooksLikeCookieProblem(result.stderr)) return result;

        if (result.stderr.Contains("dpapi", StringComparison.OrdinalIgnoreCase)) _cookiesKnownBroken = true;
        Log.Warn($"yt-dlp couldn't read browser cookies, retrying without them: {FirstError(result.stderr)}");
        return await RunProcessAsync(arguments, onLine, token).ConfigureAwait(false);
    }

    private static async Task<(int exit, string stdout, string stderr)> RunProcessAsync(
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
