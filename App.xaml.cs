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

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Dispatcher unhandled exception", e.Exception);
        MessageBox.Show(
            $"Something went wrong:\n\n{e.Exception.Message}\n\nThe error was written to the log " +
            $"({Log.Directory}). The app will try to keep running.",
            "Sink", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info($"─── Sink exiting (code {e.ApplicationExitCode}) ───");
        DownloadService.ClearPreviews();
        base.OnExit(e);
    }
}
