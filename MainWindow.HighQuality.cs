using System.IO;
using Sink.Models;
using Sink.Services;
using Sink.Services.Download;

namespace Sink;

/// <summary>
/// "Download high quality" (right-click an album, artist, genre or track).
///
/// With Settings ▸ Keep high quality on, each track's high-quality copy is its
/// kept original (or the library file itself when that's a FLAC):
///  • genuine lossless → nothing to do;
///  • a fake FLAC (lossy audio re-wrapped, see <see cref="AudioQuality"/>) →
///    a real FLAC is downloaded and the fake deleted;
///  • no lossless copy at all (an MP3 library file) → a real FLAC is
///    downloaded into the high-quality folder.
/// The library copy is then rebuilt from that FLAC in the chosen format.
///
/// With it off, the track is simply re-downloaded in the format picked on the
/// Download page and its old file deleted — unless it already is that format
/// (a fake FLAC doesn't count as FLAC).
///
/// Replacements are downloaded from Deezer (the only lossless source) and
/// update the existing library track in place, so its plays, playlists,
/// favorite and artwork stay; the old file is only deleted once the new one
/// has fully arrived.
/// </summary>
public partial class MainWindow
{
    private bool _highQualityRunning;

    private async void DownloadHighQuality(IReadOnlyList<Track> tracks)
    {
        if (_highQualityRunning) { PlaybackStatus.Text = "Already checking tracks for high quality…"; return; }
        if (tracks.Count == 0) return;
        _highQualityRunning = true;
        var keep = AppSettings.Current.KeepHighQuality;
        var options = ReadOptions();
        var targetExtension = "." + options.FormatExtension;
        var queued = new List<DownloadNode>();
        int alreadyGood = 0, noSource = 0;
        try
        {
            for (var i = 0; i < tracks.Count; i++)
            {
                var track = tracks[i];
                PlaybackStatus.Text = $"({i + 1}/{tracks.Count}) Checking quality of {track.Title}…";
                var snapshot = (track.FilePath, track.OriginalPath);
                if (!await Task.Run(() => NeedsHighQuality(snapshot.FilePath, snapshot.OriginalPath, keep, targetExtension)))
                {
                    alreadyGood++;
                    continue;
                }
                PlaybackStatus.Text = $"({i + 1}/{tracks.Count}) Finding a lossless source for {track.Title}…";
                var url = await DeezerService.FindTrackUrlAsync(track.Title, track.Artist, track.Album, track.Duration);
                if (url is null)
                {
                    noSource++;
                    Log.Info($"High quality: no Deezer match for \"{track.Title}\" by {track.Artist} ({track.Album})");
                    continue;
                }
                var node = new DownloadNode(DownloadKind.Single)
                {
                    Url = url,
                    Title = track.Title,
                    Artist = track.Artist,
                    Album = track.Album,
                    Genre = track.Genre,
                    TrackNumber = track.TrackNumber,
                    // Keep the cover the library already shows (it may have
                    // been repaired by hand) rather than whatever Deezer embeds.
                    ArtworkOverride = !string.IsNullOrWhiteSpace(track.ArtworkPath) && File.Exists(track.ArtworkPath) ? track.ArtworkPath : null,
                    ReplacesTrackId = track.Id,
                    State = DownloadState.Ready,
                    StatusText = "Ready — high quality",
                };
                _rootNodes.Add(node);
                queued.Add(node);
            }
        }
        finally { _highQualityRunning = false; }

        SaveFailedDownloadQueue();
        RefreshDownloadChrome();
        var summary = $"High quality: {queued.Count} to download, {alreadyGood} already fine"
                      + (noSource > 0 ? $", {noSource} with no lossless source found" : "");
        PlaybackStatus.Text = summary;
        Log.Info(summary);
        if (queued.Count == 0) return;
        if (_downloading)
        {
            PlaybackStatus.Text = summary + " — queued; they'll download after the current run (press Download)";
            return;
        }
        await RunDownloadQueueAsync(queued);
    }

    /// <summary>Whether a track needs a (re)download under the rules above. Blocking — runs the spectral check.</summary>
    private static bool NeedsHighQuality(string? filePath, string? originalPath, bool keepHighQuality, string targetExtension)
    {
        static bool IsFlac(string? path) =>
            path is not null && string.Equals(Path.GetExtension(path), ".flac", StringComparison.OrdinalIgnoreCase);

        if (keepHighQuality)
        {
            var highQuality = originalPath is not null && File.Exists(originalPath) ? originalPath
                : IsFlac(filePath) && File.Exists(filePath) ? filePath
                : null;
            return highQuality is null || AudioQuality.Classify(highQuality) != AudioQualityVerdict.Lossless;
        }

        if (filePath is null || !File.Exists(filePath)) return true;
        if (!string.Equals(Path.GetExtension(filePath), targetExtension, StringComparison.OrdinalIgnoreCase)) return true;
        // Already the chosen format: only a fake FLAC still needs replacing.
        return IsFlac(filePath) && AudioQuality.Classify(filePath) == AudioQualityVerdict.FakeLossless;
    }

    /// <summary>
    /// Points an existing library track at its freshly downloaded replacement
    /// and deletes the file it replaces. The new file takes the old one's
    /// name when that's free, so the folder doesn't fill with "(2)" copies.
    /// </summary>
    private void ReplaceTrackFile(Track track, string newPath, string? newOriginal)
    {
        static bool Same(string? a, string? b) =>
            a is not null && b is not null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        static void TryDelete(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn($"Couldn't delete {path}: {e.Message}"); }
        }

        var oldFile = track.FilePath;
        var oldOriginal = track.OriginalPath;
        var destination = newPath;
        if (oldFile is not null && !Same(oldFile, newPath))
        {
            TryDelete(oldFile);
            var tidy = Path.Combine(Path.GetDirectoryName(oldFile)!, Path.GetFileNameWithoutExtension(oldFile) + Path.GetExtension(newPath));
            try
            {
                if (!File.Exists(tidy)) { File.Move(newPath, tidy); destination = tidy; }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn($"Kept {newPath} (couldn't rename to {tidy}: {e.Message})"); }
        }
        track.FilePath = destination;
        track.FileName = Path.GetFileName(destination);
        track.IsMissing = false;

        if (newOriginal is not null)
        {
            if (!Same(oldOriginal, newOriginal)) TryDelete(oldOriginal);
            track.OriginalPath = newOriginal;
        }
        else if (oldOriginal is not null && AppSettings.Current.KeepHighQuality
                 && string.Equals(Path.GetExtension(destination), ".flac", StringComparison.OrdinalIgnoreCase))
        {
            // The library file is now the real FLAC, so a separate (fake) original is redundant.
            TryDelete(oldOriginal);
            track.OriginalPath = null;
        }
        Log.Info($"High quality: replaced \"{track.Title}\" — {oldFile} → {destination}" + (newOriginal is null ? "" : $" (original {newOriginal})"));
        SaveLibrary();
    }
}
