using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Sink.Services.Download;

namespace Sink.Services;

public enum AudioQualityVerdict
{
    /// <summary>File isn't there (or couldn't be read at all).</summary>
    Missing,
    /// <summary>A lossy format (MP3, AAC, Opus…).</summary>
    Lossy,
    /// <summary>A lossless container whose audio was lossy before it was converted (an "upconverted" FLAC).</summary>
    FakeLossless,
    /// <summary>Genuinely lossless audio.</summary>
    Lossless,
}

/// <summary>
/// Tells genuine lossless files apart from lossy audio that was re-wrapped as
/// FLAC. Tags can't: a real Deezer FLAC carries an ffmpeg "Lavf" encoder tag
/// just like one Sink converted from MP3. The audio can: every lossy encoder
/// applies a brick-wall low-pass (Sink's old Deezer "FLAC" files cut off at
/// ~16.5 kHz — a 30 dB cliff within half a kilohertz, then a flat floor),
/// while real CD audio rolls off gradually and keeps content up to ~21 kHz.
/// Only cliffs below 20.25 kHz count, since some genuine masters are filtered
/// steeply right around 20.5–21 kHz. Blocking — call off the UI thread.
/// </summary>
public static class AudioQuality
{
    private static readonly HashSet<string> LosslessCodecs = new(StringComparer.OrdinalIgnoreCase)
        { "flac", "alac", "ape", "wavpack", "tta", "mlp", "truehd" };

    private const int FftSize = 4096;
    private const int SampleRate = 44100;
    /// <summary>A drop of at least this many dB within 750 Hz, that never recovers, is a lossy encoder's low-pass.</summary>
    private const double CliffDb = 20;
    private const double HighestCutoffHz = 20250;

    public static AudioQualityVerdict Classify(string? path) => Analyze(path).Verdict;

    /// <param name="CutoffKHz">Where a lossy low-pass was found, when one was.</param>
    public sealed record Report(AudioQualityVerdict Verdict, string Codec, int Bits, int Rate, double? CutoffKHz);

    public static Report Analyze(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return new(AudioQualityVerdict.Missing, "", 0, 0, null);
        var (codec, bits, rate) = Probe(path);
        if (codec.Length == 0) return new(AudioQualityVerdict.Missing, "", 0, 0, null);
        if (codec.StartsWith("pcm_", StringComparison.OrdinalIgnoreCase) || LosslessCodecs.Contains(codec))
        {
            // Below CD rate can't be a lossless copy of a CD-quality source anyway.
            if (rate is > 0 and < 44100) return new(AudioQualityVerdict.FakeLossless, codec, bits, rate, null);
            // Unmeasurable (too short, silent, decode failure) counts as
            // genuine rather than risk replacing a real lossless file.
            var cutoff = LossyCutoffHz(path);
            return cutoff is { } hz
                ? new(AudioQualityVerdict.FakeLossless, codec, bits, rate, hz / 1000)
                : new(AudioQualityVerdict.Lossless, codec, bits, rate, null);
        }
        return new(AudioQualityVerdict.Lossy, codec, bits, rate, null);
    }

    private static (string Codec, int Bits, int Rate) Probe(string path)
    {
        var probe = Path.Combine(ToolManager.Directory, "ffprobe.exe");
        if (!File.Exists(probe)) return ("", 0, 0);
        var psi = new ProcessStartInfo(probe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in new[] { "-v", "error", "-select_streams", "a:0", "-show_entries",
                     "stream=codec_name,sample_rate,bits_per_raw_sample", "-of", "json", path })
            psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var json = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0) return ("", 0, 0);
            var s = streams[0];
            string Str(string n) => s.TryGetProperty(n, out var v) ? v.GetString() ?? "" : "";
            int.TryParse(Str("bits_per_raw_sample"), out var bits);
            int.TryParse(Str("sample_rate"), out var rate);
            return (Str("codec_name"), bits, rate);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return ("", 0, 0); }
    }

    /// <summary>
    /// Decodes up to 30 s from the middle of the file as mono 44.1 kHz float,
    /// averages its spectrum, and looks for a lossy encoder's low-pass: a
    /// point below 20.25 kHz where the level falls at least 20 dB within
    /// 750 Hz and everything above stays down there. Returns that frequency,
    /// or null for a genuine (or unmeasurable) file.
    /// </summary>
    private static double? LossyCutoffHz(string path)
    {
        var ffmpeg = ToolManager.FfmpegPath;
        if (!File.Exists(ffmpeg)) return null;
        var start = 30.0;
        try { using var tag = TagLib.File.Create(path); start = Math.Max(0, tag.Properties.Duration.TotalSeconds / 2 - 15); }
        catch { }
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in new[] { "-v", "error", "-ss", start.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                     "-t", "30", "-i", path, "-map", "0:a:0", "-ac", "1", "-ar", SampleRate.ToString(), "-f", "f32le", "-" })
            psi.ArgumentList.Add(a);
        float[] samples;
        try
        {
            using var p = Process.Start(psi)!;
            _ = p.StandardError.ReadToEndAsync();
            using var ms = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(ms);
            p.WaitForExit(30000);
            var bytes = ms.ToArray();
            samples = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 4);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
        if (samples.Length < FftSize * 4) return null;

        var power = new double[FftSize / 2];
        var window = new double[FftSize];
        for (var i = 0; i < FftSize; i++) window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1));
        var re = new double[FftSize];
        var im = new double[FftSize];
        var frames = 0;
        for (var offset = 0; offset + FftSize <= samples.Length; offset += FftSize / 2)
        {
            for (var i = 0; i < FftSize; i++) { re[i] = samples[offset + i] * window[i]; im[i] = 0; }
            Fft(re, im);
            for (var k = 0; k < FftSize / 2; k++) power[k] += re[k] * re[k] + im[k] * im[k];
            frames++;
        }
        double Band(double lo, double hi)
        {
            var a = (int)(lo * FftSize / SampleRate);
            var b = (int)(hi * FftSize / SampleRate);
            double sum = 0;
            for (var k = a; k <= b; k++) sum += power[k];
            return sum / (b - a + 1) / frames;
        }
        var reference = Band(11000, 15000);
        if (reference <= 1e-12) return null; // near-silent passage — can't judge
        double Level(double hz) => 10 * Math.Log10(Math.Max(Band(hz, hz + 250), 1e-30) / reference);
        for (var hz = 13000.0; hz <= HighestCutoffHz; hz += 250)
        {
            var before = Level(hz);
            if (before - Level(hz + 750) < CliffDb) continue;
            // It must stay down: a lossy low-pass leaves a flat floor, not a dip.
            var above = new List<double>();
            for (var f = hz + 750; f <= 21500; f += 250) above.Add(Level(f));
            if (above.Count > 0 && above.Max() < before - CliffDb) return hz + 250;
        }
        return null;
    }

    /// <summary>In-place iterative radix-2 FFT.</summary>
    private static void Fft(double[] re, double[] im)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = -2 * Math.PI / len;
            var wRe = Math.Cos(angle);
            var wIm = Math.Sin(angle);
            for (var i = 0; i < n; i += len)
            {
                double curRe = 1, curIm = 0;
                for (var k = 0; k < len / 2; k++)
                {
                    var uRe = re[i + k];
                    var uIm = im[i + k];
                    var vRe = re[i + k + len / 2] * curRe - im[i + k + len / 2] * curIm;
                    var vIm = re[i + k + len / 2] * curIm + im[i + k + len / 2] * curRe;
                    re[i + k] = uRe + vRe;
                    im[i + k] = uIm + vIm;
                    re[i + k + len / 2] = uRe - vRe;
                    im[i + k + len / 2] = uIm - vIm;
                    var nextRe = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = nextRe;
                }
            }
        }
    }
}
