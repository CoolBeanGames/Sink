using System.IO;
using System.Text;

namespace Sink.Services;

/// <summary>
/// Minimal append-only file logger at Documents/Sink/Logs/sink-yyyy-MM-dd.log —
/// kept under Documents so the user can get at it easily. Every line is
/// timestamped and tagged; exceptions log their full string. Cheap and
/// swallow-all so logging never becomes a failure of its own.
/// </summary>
public static class Log
{
    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Sink", "Logs");

    /// <summary>Opens the log folder in Explorer, creating it first if need be.</summary>
    public static void OpenFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Directory) { UseShellExecute = true });
        }
        catch
        {
            // opening the folder is a convenience, never worth surfacing
        }
    }

    private static readonly object Gate = new();
    private static string CurrentPath => Path.Combine(Directory, $"sink-{DateTime.Now:yyyy-MM-dd}.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    /// <summary>Wires the process-wide crash handlers. Call once at startup.</summary>
    public static void InstallGlobalHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Error("AppDomain unhandled exception" + (e.IsTerminating ? " (terminating)" : ""), e.ExceptionObject as Exception);

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(CurrentPath, $"{DateTime.Now:HH:mm:ss.fff}  {level,-5}  {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
            // logging must never throw
        }
    }

    /// <summary>Deletes log files older than <paramref name="keepDays"/> days.</summary>
    public static void Prune(int keepDays = 14)
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return;
            var cutoff = DateTime.Now.AddDays(-keepDays);
            foreach (var file in new DirectoryInfo(Directory).GetFiles("sink-*.log"))
                if (file.LastWriteTime < cutoff)
                    try { file.Delete(); } catch { }
        }
        catch { }
    }
}
