using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Sink.Services;
using Sink.Services.Download;
using Sink.Services.Ipod;

namespace Sink;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // One-shot elevated helper invocation (see IpodReader.EnsureExtendedSysInfo):
        // reading a fresh/never-elevated iPod's extended device info over SCSI needs
        // admin rights, so a non-elevated Sink relaunches itself with this to get
        // just that one file written, then exits without opening the main window.
        if (e.Args.Length == 2 && e.Args[0] == "--prepare-ipod")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var xml = DeviceSysInfoReader.Read(e.Args[1]);
                var dir = Path.Combine(e.Args[1], "iPod_Control", "Device");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "SysInfoExtended"), xml);
                Shutdown(0);
            }
            catch
            {
                Shutdown(1);
            }
            return;
        }

        Log.InstallGlobalHandlers();
        Log.Prune();
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        Log.Info($"─── Sink {version} starting ───");

        DispatcherUnhandledException += App_DispatcherUnhandledException;

        AppSettings.Load();
        base.OnStartup(e);
    }

    private readonly Queue<DateTime> _recentExceptionTimes = new();

    /// <summary>
    /// A one-off exception here is recoverable — log it, tell the user, keep
    /// going. A *recurring* one (some periodic trigger — a timer tick, a
    /// virtualized container reload — hitting the same bug every cycle) is
    /// not: each dialog's own modal message loop nests inside the last, and
    /// enough of those exhausted the call stack outright and killed the
    /// process, which is what turned two unrelated bugs into the same
    /// "cascade of 20+ dialogs then the app vanishes" crash. Past a burst
    /// threshold, stop opening more dialogs — log-only — since a dialog
    /// nobody can dismiss fast enough is worse than one that never shows.
    /// </summary>
    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Dispatcher unhandled exception", e.Exception);
        e.Handled = true;

        var now = DateTime.UtcNow;
        _recentExceptionTimes.Enqueue(now);
        while (_recentExceptionTimes.Count > 0 && now - _recentExceptionTimes.Peek() > TimeSpan.FromSeconds(10))
            _recentExceptionTimes.Dequeue();
        if (_recentExceptionTimes.Count > 5)
        {
            Log.Error($"Suppressing further error dialogs — {_recentExceptionTimes.Count} unhandled exceptions in the last 10s (see log for details)");
            return;
        }

        MessageBox.Show(
            $"Something went wrong:\n\n{e.Exception.Message}\n\nThe error was written to the log " +
            $"({Log.Directory}). The app will try to keep running.",
            "Sink", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info($"─── Sink exiting (code {e.ApplicationExitCode}) ───");
        DownloadService.ClearPreviews();
        base.OnExit(e);
    }
}
