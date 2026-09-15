using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Sink.Services;

/// <summary>
/// Files a bug directly into this project's own Zen task queue via the
/// zen-operator CLI -- the same tool an agent uses to manage zen.tasks.json.
/// Resolves the executable the same way Zen's own agent-facing instructions
/// do: try it on PATH first (Zen keeps it there automatically), and fall
/// back to the absolute path recorded in operator-path.txt for a process
/// that started before Zen last updated PATH.
/// </summary>
public static class ZenOperator
{
    public static (bool success, string message) FileBug(string title, string description, IReadOnlyList<string> tags)
    {
        var args = new List<string> { "bug", "--branch", "main", "--title", title, "--task", description };
        foreach (var tag in tags) { args.Add("--tag"); args.Add(tag); }

        var (ok, output) = Run("zen-operator", args);
        if (ok) return (true, output);

        var fallback = ResolveFallbackPath();
        if (fallback is null) return (false, output);
        return Run(fallback, args);
    }

    private static (bool success, string output) Run(string exe, List<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                // zen-operator finds zen.tasks.json by walking up from the
                // working directory -- Sink's own build output already sits
                // inside the repo, so this reaches it the same way it would
                // from a shell opened anywhere under the project.
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var process = Process.Start(psi);
            if (process is null) return (false, "Couldn't start zen-operator");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(15000);
            return process.ExitCode == 0
                ? (true, stdout.Trim())
                : (false, string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim());
        }
        catch (Win32Exception)
        {
            return (false, $"\"{exe}\" not found");
        }
        catch (Exception ex)
        {
            Log.Error("ZenOperator run failed", ex);
            return (false, ex.Message);
        }
    }

    private static string? ResolveFallbackPath()
    {
        try
        {
            var fallbackFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zen", "operator-path.txt");
            if (!File.Exists(fallbackFile)) return null;
            var path = File.ReadAllText(fallbackFile).Trim();
            return File.Exists(path) ? path : null;
        }
        catch { return null; }
    }
}
