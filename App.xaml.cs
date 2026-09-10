using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Sink.Services;
using Sink.Services.Download;

namespace Sink;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
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
