using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Sink.Services.Download;

/// <summary>Downloads Deezer queue items through the managed Streamrip tool.</summary>
public static partial class StreamripService
{
    public static async Task<IReadOnlyList<string>> DownloadAsync(
        DownloadNode node, DownloadOptions options, IProgress<double> progress,
        IProgress<string>? status = null, IProgress<string>? onTrackFile = null,
        IProgress<DownloadNode>? onTrackDone = null, CancellationToken token = default)
    {
        var toolProgress = new Progress<string>(message => status?.Report(message));
        await StreamripToolManager.EnsureAsync(toolProgress, token).ConfigureAwait(false);

        Directory.CreateDirectory(DownloadService.DownloadsDirectory);
        var workRoot = Path.Combine(
            DownloadService.DownloadsDirectory, "_streamrip_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workRoot);

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        void OnUi(Action action)
        {
            if (dispatcher is null || dispatcher.CheckAccess()) action();
            else dispatcher.Invoke(action);
        }

        var isCollection = node.Kind == DownloadKind.Album;
        var selected = isCollection
            ? node.Children.Where(t => t.Kind == DownloadKind.Track && t.Enabled == true && t.State != DownloadState.Done)
                .OrderBy(t => t.Index).ToList()
            : [node];
        if (selected.Count == 0) throw new InvalidOperationException("No Deezer tracks are selected");
        if (selected.Any(t => string.IsNullOrWhiteSpace(t.Url)))
            throw new InvalidOperationException("This saved Deezer item is missing its track links; remove it and add the Deezer link again");

        var artOverride = node.ArtworkOverride ?? node.Parent?.ArtworkOverride;
        var artBytes = string.IsNullOrWhiteSpace(artOverride) ? null : Artwork.SquareCropBytes(artOverride);
        var finished = new List<string>();
        var failures = new List<string>();

        try
        {
            for (var i = 0; i < selected.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var track = selected[i];
                var itemDirectory = Path.Combine(workRoot, (i + 1).ToString("000"));
                Directory.CreateDirectory(itemDirectory);
                progress.Report((double)i / selected.Count);
                OnUi(() =>
                {
                    track.State = DownloadState.Downloading;
                    track.StatusText = "Downloading from Deezer…";
                });
                status?.Report(selected.Count == 1
                    ? $"Downloading {track.Name} from Deezer"
                    : $"Downloading track {i + 1}/{selected.Count} from Deezer");

                try
                {
                    var args = new List<string>
                    {
                        "--config-path", StreamripToolManager.ConfigPath,
                        "--folder", itemDirectory,
                        "--no-db",
                        "--quality", StreamripQuality(options).ToString(),
                        "--no-progress",
                        "url", track.Url,
                    };
                    var (exit, stdout, stderr) = await RunRipAsync(args, token).ConfigureAwait(false);
                    var source = Directory.EnumerateFiles(itemDirectory, "*", SearchOption.AllDirectories)
                        .Where(path => DownloadService.AudioExtensions.Contains(Path.GetExtension(path)))
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .FirstOrDefault();
                    if (exit != 0 || source is null)
                        throw new InvalidOperationException(StreamripError(stderr, stdout)
                                                            ?? "Streamrip did not produce an audio file");

                    var converted = await ConvertToRequestedFormatAsync(source, options, token).ConfigureAwait(false);
                    var (artist, album, genre) = EffectiveMetadata(node, track);
                    var trackNo = track.TrackNumber > 0 ? track.TrackNumber
                        : options.NumberTracks && isCollection ? track.Index : 0;
                    if (options.WriteMetadata || artBytes is { Length: > 0 } || !options.EmbedAlbumArt)
                    {
                        DownloadService.ApplyTags(
                            converted,
                            options.WriteMetadata ? artist : "",
                            options.WriteMetadata ? album : "",
                            options.WriteMetadata ? genre : "",
                            options.WriteMetadata ? track.Title : null,
                            options.WriteMetadata ? trackNo : 0,
                            artBytes,
                            clearArtwork: !options.EmbedAlbumArt);
                    }

                    var destinationDirectory = MusicImporter.ArtistAlbumDir(
                        DownloadService.DownloadsDirectory, artist, album);
                    Directory.CreateDirectory(destinationDirectory);
                    var stem = isCollection
                        ? $"{Math.Max(1, track.Index):000} - {track.Title}"
                        : $"{artist} - {track.Title}";
                    var finalPath = DownloadService.UniquePath(Path.Combine(
                        destinationDirectory,
                        DownloadService.Sanitize(stem) + Path.GetExtension(converted)));
                    File.Move(converted, finalPath, overwrite: false);
                    finished.Add(finalPath);
                    onTrackFile?.Report(finalPath);
                    if (track.Kind == DownloadKind.Track)
                    {
                        OnUi(() =>
                        {
                            track.State = DownloadState.Done;
                            track.Progress = 1;
                            track.StatusText = "Done";
                            onTrackDone?.Report(track);
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failures.Add($"{track.Name}: {ex.Message}");
                    OnUi(() =>
                    {
                        track.State = DownloadState.Failed;
                        track.StatusText = $"Failed — {Shorten(ex.Message)}";
                    });
                    Log.Warn($"Streamrip failed for {track.Name} ({track.Url}): {ex.Message}");
                }
                finally
                {
                    TryDeleteDirectory(itemDirectory);
                }
                progress.Report((double)(i + 1) / selected.Count);
            }
        }
        finally
        {
            TryDeleteDirectory(workRoot);
        }

        if (finished.Count == 0)
            throw new InvalidOperationException(failures.FirstOrDefault() ?? "Streamrip did not download any selected tracks");
        if (failures.Count > 0)
            throw new DownloadService.PartialDownloadException(finished, string.Join("; ", failures));
        return finished;
    }

    private static (string Artist, string Album, string Genre) EffectiveMetadata(
        DownloadNode node, DownloadNode track)
    {
        if (node.Kind == DownloadKind.Album)
        {
            var artist = node.IsMixedPlaylist
                ? DownloadService.FirstReal(track.Artist) ?? "Unknown Artist"
                : DownloadService.FirstReal(node.Artist, node.Parent?.Artist) ?? "Unknown Artist";
            var album = node.IsMixedPlaylist ? track.Album : node.Album;
            var genre = node.IsMixedPlaylist ? track.Genre : node.Genre;
            return (artist, album, DownloadService.FirstReal(genre) ?? "Unknown");
        }
        return (
            DownloadService.FirstReal(node.Artist, node.Parent?.Artist) ?? "Unknown Artist",
            node.Album,
            DownloadService.FirstReal(node.Genre, node.Parent?.Genre) ?? "Unknown");
    }

    private static int StreamripQuality(DownloadOptions options)
    {
        if (options.Quality <= 0) return 2;
        return options.Quality >= 320 ? 1 : 0;
    }

    private static async Task<string> ConvertToRequestedFormatAsync(
        string source, DownloadOptions options, CancellationToken token)
    {
        var wantedExtension = "." + options.FormatExtension;
        if (string.Equals(Path.GetExtension(source), wantedExtension, StringComparison.OrdinalIgnoreCase))
            return source;
        if (!File.Exists(ToolManager.FfmpegPath))
            throw new InvalidOperationException("ffmpeg is not installed yet");

        var output = Path.Combine(
            Path.GetDirectoryName(source)!,
            Path.GetFileNameWithoutExtension(source) + ".sink-converted" + wantedExtension);
        var psi = new ProcessStartInfo(ToolManager.FfmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", source,
                     "-map", "0:a:0", "-map_metadata", "0", "-vn" })
            psi.ArgumentList.Add(argument);
        foreach (var argument in CodecArguments(options)) psi.ArgumentList.Add(argument);
        psi.ArgumentList.Add(output);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg");
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0 || !File.Exists(output))
            throw new InvalidOperationException(FirstNonEmptyLine(error) ?? "ffmpeg could not convert the Deezer download");
        try { File.Delete(source); } catch (IOException) { }
        return output;
    }

    private static IReadOnlyList<string> CodecArguments(DownloadOptions options)
    {
        var bitrate = Math.Clamp(options.Quality > 0 ? options.Quality : 320, 64, 320) + "k";
        return options.Format switch
        {
            AudioFormat.Mp3 => ["-c:a", "libmp3lame", "-b:a", bitrate],
            AudioFormat.M4a => ["-c:a", "aac", "-b:a", bitrate],
            AudioFormat.Opus => ["-c:a", "libopus", "-b:a", bitrate],
            AudioFormat.Flac => ["-c:a", "flac"],
            AudioFormat.Wav => ["-c:a", "pcm_s16le"],
            _ => ["-c:a", "libmp3lame", "-b:a", bitrate],
        };
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunRipAsync(
        IReadOnlyList<string> arguments, CancellationToken token)
    {
        if (!File.Exists(StreamripToolManager.RipPath))
            throw new InvalidOperationException("Streamrip is not installed yet");
        var psi = new ProcessStartInfo(StreamripToolManager.RipPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        StreamripToolManager.ApplyManagedEnvironment(psi);
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start Streamrip");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static string? StreamripError(string stderr, string stdout)
    {
        var lines = AnsiEscape().Replace(stderr + "\n" + stdout, "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.FirstOrDefault(line =>
                   line.Contains("error", StringComparison.OrdinalIgnoreCase)
                   || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
                   || line.Contains("could not", StringComparison.OrdinalIgnoreCase))
               ?? lines.LastOrDefault();
    }

    private static string Shorten(string value) => value.Length <= 120 ? value : value[..117] + "…";

    private static string? FirstNonEmptyLine(string value) =>
        value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex AnsiEscape();
}
