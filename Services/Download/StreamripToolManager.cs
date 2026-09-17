using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sink.Services.Download;

/// <summary>
/// Installs Streamrip without assuming Python or pip is already present. A
/// checksum-verified standalone uv binary owns an isolated Python 3.12 and
/// Streamrip environment beneath Sink's existing managed tools directory.
/// </summary>
public static class StreamripToolManager
{
    public static string Root { get; } = Path.Combine(ToolManager.Directory, "streamrip");
    public static string UvPath => Path.Combine(Root, "uv.exe");
    public static string RipPath => Path.Combine(Root, "bin", "rip.exe");
    public static string ConfigPath => Path.Combine(Root, "config.toml");
    public static bool Present => File.Exists(UvPath) && File.Exists(RipPath);

    private const string UvLatestApi = "https://api.github.com/repos/astral-sh/uv/releases/latest";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Sink/1.0 (+https://github.com)");
        return client;
    }

    public static async Task EnsureAsync(IProgress<string> status, CancellationToken token = default)
    {
        if (Present) { status.Report("Streamrip is ready"); return; }

        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (Present) { status.Report("Streamrip is ready"); return; }
            Directory.CreateDirectory(Root);

            if (!File.Exists(UvPath))
                await InstallUvAsync(status, token).ConfigureAwait(false);

            status.Report("Installing Streamrip and its managed Python runtime…");
            var (exit, stdout, stderr) = await RunUvAsync(
                ["--no-progress", "tool", "install", "--python", "3.12", "streamrip"], token).ConfigureAwait(false);
            if (exit != 0 || !File.Exists(RipPath))
            {
                var reason = FirstLine(stderr) ?? FirstLine(stdout) ?? "uv did not create rip.exe";
                throw new InvalidOperationException($"Could not install Streamrip: {reason}");
            }
            status.Report("Streamrip ready");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException
                                  or InvalidDataException or JsonException or InvalidOperationException)
        {
            status.Report($"Could not set up Streamrip: {e.Message}");
            throw;
        }
        finally
        {
            Gate.Release();
        }
    }

    internal static void ApplyManagedEnvironment(ProcessStartInfo psi)
    {
        var cache = Path.Combine(Root, "cache");
        var python = Path.Combine(Root, "python");
        var pythonBin = Path.Combine(Root, "python-bin");
        var tools = Path.Combine(Root, "tools");
        var bin = Path.Combine(Root, "bin");
        Directory.CreateDirectory(cache);
        Directory.CreateDirectory(python);
        Directory.CreateDirectory(pythonBin);
        Directory.CreateDirectory(tools);
        Directory.CreateDirectory(bin);

        psi.Environment["UV_CACHE_DIR"] = cache;
        psi.Environment["UV_PYTHON_INSTALL_DIR"] = python;
        psi.Environment["UV_PYTHON_BIN_DIR"] = pythonBin;
        psi.Environment["UV_TOOL_DIR"] = tools;
        psi.Environment["UV_TOOL_BIN_DIR"] = bin;
        psi.Environment["UV_PYTHON_PREFERENCE"] = "only-managed";
        psi.Environment["UV_PYTHON_NO_REGISTRY"] = "1";
        psi.Environment["UV_NO_PROGRESS"] = "1";
        psi.Environment["PATH"] = ToolManager.Directory + Path.PathSeparator
                                  + (psi.Environment.TryGetValue("PATH", out var path) ? path : "");
    }

    private static async Task InstallUvAsync(IProgress<string> status, CancellationToken token)
    {
        status.Report("Downloading the Streamrip installer…");
        using var release = JsonDocument.Parse(await Http.GetStringAsync(UvLatestApi, token).ConfigureAwait(false));
        if (!release.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The uv release did not contain downloadable assets");

        var archiveName = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "uv-aarch64-pc-windows-msvc.zip",
            Architecture.X86 => "uv-i686-pc-windows-msvc.zip",
            _ => "uv-x86_64-pc-windows-msvc.zip",
        };
        var archiveUrl = FindAssetUrl(assets, archiveName)
                         ?? throw new InvalidDataException($"The uv release is missing {archiveName}");
        var checksumUrl = FindAssetUrl(assets, archiveName + ".sha256")
                          ?? throw new InvalidDataException($"The uv release is missing {archiveName}.sha256");

        var archiveTask = Http.GetByteArrayAsync(archiveUrl, token);
        var checksumTask = Http.GetStringAsync(checksumUrl, token);
        await Task.WhenAll(archiveTask, checksumTask).ConfigureAwait(false);
        var archive = await archiveTask.ConfigureAwait(false);
        var expected = checksumTask.Result.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (expected is null || expected.Length != 64)
            throw new InvalidDataException("The uv checksum file was invalid");
        var actual = Convert.ToHexStringLower(SHA256.HashData(archive));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The uv download failed checksum verification");

        using var memory = new MemoryStream(archive, writable: false);
        using var zip = new ZipArchive(memory, ZipArchiveMode.Read);
        var entry = zip.Entries.FirstOrDefault(e =>
            string.Equals(Path.GetFileName(e.FullName), "uv.exe", StringComparison.OrdinalIgnoreCase));
        if (entry is null) throw new InvalidDataException("The uv archive did not contain uv.exe");

        var temporaryPath = UvPath + ".download";
        try
        {
            await using (var source = entry.Open())
            await using (var destination = new FileStream(
                             temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await source.CopyToAsync(destination, token).ConfigureAwait(false);
            File.Move(temporaryPath, UvPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
        }
        status.Report("Streamrip installer ready");
    }

    private static string? FindAssetUrl(JsonElement assets, string name)
    {
        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var assetName)
                || !string.Equals(assetName.GetString(), name, StringComparison.OrdinalIgnoreCase)) continue;
            if (asset.TryGetProperty("browser_download_url", out var url)) return url.GetString();
        }
        return null;
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunUvAsync(
        IReadOnlyList<string> arguments, CancellationToken token)
    {
        var psi = new ProcessStartInfo(UvPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        ApplyManagedEnvironment(psi);
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the Streamrip installer");
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
    public static string GetDeezerArl()
    {
        if (!File.Exists(ConfigPath)) return "";
        foreach (var line in File.ReadLines(ConfigPath))
        {
            if (line.TrimStart().StartsWith("arl", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Split('=', 2);
                if (parts.Length == 2)
                {
                    return parts[1].Trim().Trim('"', '\'');
                }
            }
        }
        return "";
    }

    public static void SetDeezerArl(string arl)
    {
        if (!File.Exists(ConfigPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, "[deezer]\narl = \"" + arl + "\"\n");
            return;
        }

        var lines = File.ReadAllLines(ConfigPath).ToList();
        var deezerSectionIdx = -1;
        var arlLineIdx = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Equals("[deezer]", StringComparison.OrdinalIgnoreCase))
                deezerSectionIdx = i;
            else if (deezerSectionIdx != -1 && trimmed.StartsWith("[") && trimmed.EndsWith("]"))
            {
                if (arlLineIdx == -1) deezerSectionIdx = -1;
            }
            else if (deezerSectionIdx != -1 && trimmed.StartsWith("arl", StringComparison.OrdinalIgnoreCase))
            {
                var beforeEq = lines[i].Split('=')[0];
                if (beforeEq.Trim().Equals("arl", StringComparison.OrdinalIgnoreCase))
                    arlLineIdx = i;
            }
        }

        if (arlLineIdx != -1)
        {
            lines[arlLineIdx] = "arl = \"" + arl + "\"";
        }
        else if (deezerSectionIdx != -1)
        {
            lines.Insert(deezerSectionIdx + 1, "arl = \"" + arl + "\"");
        }
        else
        {
            lines.Add("");
            lines.Add("[deezer]");
            lines.Add("arl = \"" + arl + "\"");
        }

        File.WriteAllLines(ConfigPath, lines);
    }
    private static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
}
