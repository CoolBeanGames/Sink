using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Sink.Services.Download;

/// <summary>One track resolved from a Spotify link, with its Album already worked out (task 168 — see <see cref="SunnifyService.ResolveAsync"/>).</summary>
public sealed record SpotifyTrack(string Title, string Artist, string Album);

/// <summary>A resolved Spotify link: "track" (one <see cref="Tracks"/> entry) or "album"/"playlist" (many).</summary>
public sealed record SpotifyResolved(string Type, string Name, IReadOnlyList<SpotifyTrack> Tracks);

/// <summary>
/// Detects Spotify links and resolves them to plain title/artist metadata via
/// the Sunnify CLI's keyless <c>info --json</c> (no Spotify account or API
/// key needed — it reads Spotify's public embed pages, same as the app
/// itself). Never downloads anything through Sunnify; see
/// <see cref="SunnifyToolManager"/> for why.
/// </summary>
public static class SunnifyService
{
    /// <summary>Loose on purpose — Sunnify itself reports a precise "invalid_url" for anything it can't actually handle (e.g. an artist link), which <see cref="ResolveAsync"/> surfaces as-is.</summary>
    public static bool IsSpotifyLink(string url) =>
        url.Contains("open.spotify.com/", StringComparison.OrdinalIgnoreCase)
        || url.TrimStart().StartsWith("spotify:", StringComparison.OrdinalIgnoreCase);

    public static async Task<SpotifyResolved> ResolveAsync(string url, CancellationToken token = default)
    {
        var (exit, stdout, stderr) = await RunAsync(["info", url, "--json"], token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(stdout))
            throw new InvalidOperationException(exit == 0 ? "Sunnify returned nothing" : FirstNonEmptyLine(stderr) ?? "Sunnify could not resolve that link");
        return Parse(stdout);
    }

    private static SpotifyResolved Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("event", out var evt) && evt.ValueKind == JsonValueKind.String && evt.GetString() == "error")
            throw new InvalidOperationException(ErrorMessage(root));

        var type = Str(root, "type");
        switch (type)
        {
            case "track":
            {
                var title = Str(root, "title");
                var artist = Str(root, "artists");
                var album = Str(root, "album");
                return new SpotifyResolved("track", title, [new SpotifyTrack(title, artist, album)]);
            }
            case "album" or "playlist":
            {
                var name = Str(root, "name");
                var tracks = new List<SpotifyTrack>();
                if (root.TryGetProperty("tracks", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var t in arr.EnumerateArray())
                        // An album's tracks all share its one real album name; a
                        // playlist mixes artists/albums and Sunnify doesn't hand
                        // back a per-track album at all, so that's left blank —
                        // same "don't force one shared tag" idea as a mixed
                        // YouTube playlist (task 151).
                        tracks.Add(new SpotifyTrack(Str(t, "title"), Str(t, "artists"), type == "album" ? name : ""));
                return new SpotifyResolved(type, name, tracks);
            }
            default:
                throw new InvalidOperationException($"Unrecognized Spotify link type: \"{type}\"");
        }
    }

    private static string ErrorMessage(JsonElement root)
    {
        var message = Str(root, "message");
        var hint = Str(root, "hint");
        return hint.Length > 0 ? $"{message} — {hint}" : (message.Length > 0 ? message : "Sunnify couldn't resolve that link");
    }

    private static string Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string? FirstNonEmptyLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    private static async Task<(int exit, string stdout, string stderr)> RunAsync(IReadOnlyList<string> arguments, CancellationToken token)
    {
        if (!File.Exists(SunnifyToolManager.SunnifyPath))
            throw new InvalidOperationException("Sunnify is not installed yet");

        var psi = new ProcessStartInfo(SunnifyToolManager.SunnifyPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Without this, .NET decodes Sunnify's UTF-8 stdout using the
            // system's single-byte console codepage — every non-ASCII title
            // (Japanese, accented Latin, etc.) came out as mojibake, and once
            // mangled that way it's no longer recognizable as any real
            // language, so "translate to English" had nothing to detect.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start Sunnify");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(token);
        var stderrTask = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token).ConfigureAwait(false);
        return (process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }
}
