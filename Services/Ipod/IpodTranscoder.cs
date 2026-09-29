using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using Sink.Services.Download;

namespace Sink.Services.Ipod;

/// <summary>
/// Converts library files the iPod can't play (FLAC, Opus, Ogg…) into a format
/// it can, just for the copy onto the device — the library file is never
/// touched. Lossless sources become Apple Lossless (16-bit, at most 48 kHz,
/// which is what iPod firmware decodes); lossy ones become 256 kbps AAC.
/// Uses the ffmpeg that Sink already manages for downloads.
/// </summary>
public static class IpodTranscoder
{
    private static readonly HashSet<string> Lossless = new(StringComparer.OrdinalIgnoreCase)
        { ".flac", ".ape", ".wv", ".tta", ".aif", ".aiff" };
    private static readonly HashSet<string> Lossy = new(StringComparer.OrdinalIgnoreCase)
        { ".opus", ".ogg", ".oga", ".webm", ".wma", ".mka" };

    private static readonly string TempDirectory = Path.Combine(Path.GetTempPath(), "Sink-ipod-transcode");

    public static bool CanTranscode(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path) && File.Exists(ToolManager.FfmpegPath) &&
        (Lossless.Contains(Path.GetExtension(path)) || Lossy.Contains(Path.GetExtension(path)));

    public static bool NeedsTranscode(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        (Lossless.Contains(Path.GetExtension(path)) || Lossy.Contains(Path.GetExtension(path)));

    /// <summary>Converts <paramref name="source"/> to a temporary .m4a and returns its path. The caller deletes it once it's been copied.</summary>
    public static string Transcode(string source, CancellationToken token)
    {
        Directory.CreateDirectory(TempDirectory);
        var output = Path.Combine(TempDirectory, $"{Guid.NewGuid():N}.m4a");
        var psi = new ProcessStartInfo(ToolManager.FfmpegPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", source,
                     "-map", "0:a:0", "-map_metadata", "0", "-vn" })
            psi.ArgumentList.Add(arg);
        if (Lossless.Contains(Path.GetExtension(source)))
        {
            foreach (var arg in new[] { "-c:a", "alac", "-sample_fmt", "s16p" }) psi.ArgumentList.Add(arg);
            if (SampleRate(source) > 48000) { psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add("44100"); }
        }
        else
        {
            foreach (var arg in new[] { "-c:a", "aac", "-b:a", "256k" }) psi.ArgumentList.Add(arg);
        }
        psi.ArgumentList.Add(output);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg");
        var stderr = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();
        while (!process.WaitForExit(200))
        {
            if (!token.IsCancellationRequested) continue;
            try { process.Kill(entireProcessTree: true); } catch { }
            TryDelete(output);
            token.ThrowIfCancellationRequested();
        }
        if (process.ExitCode != 0 || !File.Exists(output))
        {
            TryDelete(output);
            var error = stderr.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            throw new InvalidOperationException($"ffmpeg couldn't convert {Path.GetFileName(source)}: {error ?? "unknown error"}");
        }
        return output;
    }

    private static int SampleRate(string path)
    {
        try { using var file = TagLib.File.Create(path); return file.Properties.AudioSampleRate; }
        catch { return 0; }
    }

    public static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { File.Delete(path); } catch { }
    }

    /// <summary>
    /// Converts upcoming files a few at a time in the background while the
    /// sync loop copies the current one, so a large FLAC library isn't
    /// converted strictly one file after another.
    /// </summary>
    public sealed class Prefetcher(CancellationToken token) : IDisposable
    {
        private readonly ConcurrentDictionary<string, Task<string>> _jobs = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _slots = new(Math.Clamp(Environment.ProcessorCount / 2, 1, 4));

        public void Queue(string source) =>
            _jobs.GetOrAdd(source, s => Task.Run(async () =>
            {
                await _slots.WaitAsync(token);
                try { return Transcode(s, token); }
                finally { _slots.Release(); }
            }, token));

        /// <summary>The converted file for <paramref name="source"/>, waiting for (or starting) its conversion.</summary>
        public string Take(string source)
        {
            Queue(source);
            _jobs.TryRemove(source, out var job);
            return job!.GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            // Anything converted ahead but never used (cancelled, out of space).
            foreach (var job in _jobs.Values)
            {
                try { job.Wait(); } catch { }
                if (job.IsCompletedSuccessfully) TryDelete(job.Result);
            }
            _jobs.Clear();
        }
    }
}
