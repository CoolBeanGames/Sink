using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace Sink.Services.Download;

/// <summary>
/// Keeps a local copy of the Sunnify CLI (sunnify-spotify-downloader) under
/// %AppData%/Sink/tools, installed lazily the first time a Spotify link is
/// pasted rather than eagerly like yt-dlp/ffmpeg — most users never paste
/// one, and the binary is large (task 168). Only used for
/// <c>sunnify info --json</c> (metadata resolution); the actual audio
/// download still goes through Sink's own yt-dlp pipeline via a YouTube
/// search built from that metadata, matching "even if it resolves to
/// youtube links like sunnify does" from the task and giving 100% of
/// yt-dlp's own download behavior for free.
/// </summary>
public static class SunnifyToolManager
{
    public static string SunnifyPath => Path.Combine(ToolManager.Directory, "sunnify.exe");

    private const string Repo = "sunnypatell/sunnify-spotify-downloader";
    private const string AssetName = "Sunnify-Windows-CLI.exe";
    private static string ExeUrl => $"https://github.com/{Repo}/releases/latest/download/{AssetName}";
    private static string ChecksumsUrl => $"https://github.com/{Repo}/releases/latest/download/checksums.txt";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Sink/1.0 (+https://github.com)");
        return client;
    }

    public static bool Present => File.Exists(SunnifyPath);

    /// <summary>Downloads and SHA256-verifies the Sunnify CLI if it isn't already present. Never re-downloads once installed — re-run "Update Sunnify" manually isn't wired up; a missing/corrupt file is simply re-fetched.</summary>
    public static async Task EnsureAsync(IProgress<string> status, CancellationToken token = default)
    {
        if (Present) { status.Report("Sunnify is ready"); return; }

        try
        {
            status.Report("Downloading Sunnify (for Spotify links)…");
            var bytes = await Http.GetByteArrayAsync(ExeUrl, token).ConfigureAwait(false);
            var checksums = await Http.GetStringAsync(ChecksumsUrl, token).ConfigureAwait(false);

            var expected = FindChecksum(checksums, AssetName);
            if (expected is null)
                throw new InvalidOperationException("Sunnify release is missing a checksum entry");
            var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Sunnify download failed checksum verification");

            Directory.CreateDirectory(ToolManager.Directory);
            await File.WriteAllBytesAsync(SunnifyPath, bytes, token).ConfigureAwait(false);
            status.Report("Sunnify ready");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            status.Report($"Could not set up Sunnify: {e.Message}");
        }
    }

    /// <summary>Finds the hex digest for <paramref name="fileName"/> in a "checksums.txt" (one "&lt;hex&gt;  &lt;name&gt;" line per file).</summary>
    private static string? FindChecksum(string checksumsText, string fileName)
    {
        foreach (var line in checksumsText.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || !trimmed.EndsWith(fileName, StringComparison.OrdinalIgnoreCase)) continue;
            var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && string.Equals(parts[^1], fileName, StringComparison.OrdinalIgnoreCase))
                return parts[0];
        }
        return null;
    }
}
