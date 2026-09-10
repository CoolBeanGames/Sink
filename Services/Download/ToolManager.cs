using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace Sink.Services.Download;

/// <summary>
/// Keeps a local copy of yt-dlp and ffmpeg under %AppData%/Sink/tools. On open
/// the download page calls <see cref="EnsureAsync"/>, which downloads yt-dlp
/// (a bare .exe) and ffmpeg (a zip we unpack) when they are missing or stale.
/// </summary>
public static class ToolManager
{
    public static string Directory { get; } = Path.Combine(LibraryStore.Directory, "tools");

    public static string YtDlpPath => Path.Combine(Directory, "yt-dlp.exe");
    public static string FfmpegPath => Path.Combine(Directory, "ffmpeg.exe");

    private static string StatePath => Path.Combine(Directory, "tools.json");
    private const string YtDlpExeUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    private const string YtDlpApiUrl = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";
    private const string FfmpegZipUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

    private sealed class ToolState
    {
        public string? YtDlpVersion { get; set; }
        public DateTime FfmpegChecked { get; set; }
    }

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Sink/1.0 (+https://github.com)");
        return client;
    }

    public static bool ToolsPresent => File.Exists(YtDlpPath) && File.Exists(FfmpegPath);

    /// <summary>
    /// Ensures both tools are present and reasonably current. Reports human
    /// readable progress through <paramref name="status"/>. Never throws for a
    /// plain network failure when a usable copy already exists.
    /// </summary>
    public static async Task EnsureAsync(IProgress<string> status, CancellationToken token = default)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var state = LoadState();

        await EnsureYtDlpAsync(state, status, token).ConfigureAwait(false);
        await EnsureFfmpegAsync(state, status, token).ConfigureAwait(false);

        SaveState(state);
        status.Report(ToolsPresent ? "yt-dlp and ffmpeg are ready" : "Some tools are still missing");
    }

    private static async Task EnsureYtDlpAsync(ToolState state, IProgress<string> status, CancellationToken token)
    {
        string? latest = null;
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(YtDlpApiUrl, token).ConfigureAwait(false));
            latest = doc.RootElement.GetProperty("tag_name").GetString();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { }

        var haveExe = File.Exists(YtDlpPath);
        var upToDate = haveExe && latest is not null && string.Equals(state.YtDlpVersion, latest, StringComparison.OrdinalIgnoreCase);
        if (haveExe && (upToDate || latest is null))
        {
            status.Report(haveExe ? $"yt-dlp {state.YtDlpVersion ?? "(local)"} is current" : "yt-dlp is current");
            return;
        }

        try
        {
            status.Report(haveExe ? $"Updating yt-dlp to {latest}…" : "Downloading yt-dlp…");
            var bytes = await Http.GetByteArrayAsync(YtDlpExeUrl, token).ConfigureAwait(false);
            await File.WriteAllBytesAsync(YtDlpPath, bytes, token).ConfigureAwait(false);
            state.YtDlpVersion = latest;
            status.Report($"yt-dlp {latest ?? "downloaded"} ready");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            if (!haveExe) status.Report("Could not download yt-dlp — check your connection");
        }
    }

    private static async Task EnsureFfmpegAsync(ToolState state, IProgress<string> status, CancellationToken token)
    {
        var haveExe = File.Exists(FfmpegPath);
        var fresh = haveExe && (DateTime.UtcNow - state.FfmpegChecked) < TimeSpan.FromDays(14);
        if (fresh)
        {
            status.Report("ffmpeg is current");
            return;
        }

        try
        {
            status.Report(haveExe ? "Checking ffmpeg…" : "Downloading ffmpeg (this can take a minute)…");
            var zipBytes = await Http.GetByteArrayAsync(FfmpegZipUrl, token).ConfigureAwait(false);
            var temp = Path.Combine(Directory, "ffmpeg-download.zip");
            await File.WriteAllBytesAsync(temp, zipBytes, token).ConfigureAwait(false);
            status.Report("Unpacking ffmpeg…");
            ExtractBinaries(temp);
            try { File.Delete(temp); } catch (IOException) { }
            state.FfmpegChecked = DateTime.UtcNow;
            status.Report("ffmpeg ready");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidDataException)
        {
            if (!haveExe) status.Report("Could not download ffmpeg — check your connection");
        }
    }

    /// <summary>Pulls just ffmpeg.exe / ffprobe.exe out of the release zip, flattening the archive's bin folder.</summary>
    private static void ExtractBinaries(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var name = Path.GetFileName(entry.FullName);
            if (name is not ("ffmpeg.exe" or "ffprobe.exe")) continue;
            entry.ExtractToFile(Path.Combine(Directory, name), overwrite: true);
        }
    }

    private static ToolState LoadState()
    {
        try
        {
            if (File.Exists(StatePath))
                return JsonSerializer.Deserialize<ToolState>(File.ReadAllText(StatePath)) ?? new ToolState();
        }
        catch (Exception e) when (e is IOException or JsonException) { }
        return new ToolState();
    }

    private static void SaveState(ToolState state)
    {
        try { File.WriteAllText(StatePath, JsonSerializer.Serialize(state)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
