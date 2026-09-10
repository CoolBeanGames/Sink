using System.Windows;
using Sink.Services;

namespace Sink;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppSettings.Load();
        base.OnStartup(e);
    }
}
