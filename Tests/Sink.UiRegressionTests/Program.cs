using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Path = System.Windows.Shapes.Path;
using System.Windows.Threading;
using System.Xml.Linq;
using Sink.Controls;
using Track = Sink.Models.Track;

internal static class Program
{
    private static int _passed;

    [STAThread]
    public static int Main()
    {
        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources = ReadIconStyles();
            var track = NewTrack("Song");
            var notifications = new List<bool>();
            var item = ShuffleMenu.Create([track], excluded => { notifications.Add(excluded); return Task.CompletedTask; });
            var icon = new Path { Style = (Style)app.FindResource("TrackShuffleIcon"), DataContext = track };
            FlushBindings(icon);
            Check(item.Header.Equals("Exclude from shuffle"), "initial included track offers Exclude");
            Check(ColorOf(icon) == "#FFA79AFF", "initial included track has a purple icon");

            Click(item); FlushBindings(icon);
            Check(track.ExcludedFromShuffle, "click changes the real track model to excluded");
            Check(item.Header.Equals("Include in shuffle"), "same menu item immediately switches to Include");
            Check(ColorOf(icon) == "#FF626A79", "track icon immediately turns grey");
            Check(icon.ToolTip?.ToString() == "Excluded from shuffle", "track tooltip changes with the setting");

            Click(item); FlushBindings(icon);
            Check(!track.ExcludedFromShuffle, "second click on the same menu item includes the track again");
            Check(item.Header.Equals("Exclude from shuffle"), "same menu item switches back to Exclude");
            Check(ColorOf(icon) == "#FFA79AFF", "track icon immediately turns purple again");
            Check(notifications.SequenceEqual(new[] { true, false }), "preference callbacks receive both toggle directions");

            track.ExcludedFromShuffle = true; FlushBindings(icon);
            Check(item.Header.Equals("Include in shuffle"), "external model changes also refresh an existing menu item");
            var reopened = ShuffleMenu.Create([track], _ => Task.CompletedTask);
            Check(reopened.Header.Equals("Include in shuffle"), "reopening the context menu reads the current model");

            var second = NewTrack("Other song");
            var mixed = ShuffleMenu.Create([track, second], _ => Task.CompletedTask);
            Check(mixed.Header.Equals("Exclude from shuffle"), "mixed selection offers Exclude");
            Click(mixed);
            Check(track.ExcludedFromShuffle && second.ExcludedFromShuffle && mixed.Header.Equals("Include in shuffle"), "group click excludes every selected track and changes its label");
            Click(mixed);
            Check(!track.ExcludedFromShuffle && !second.ExcludedFromShuffle && mixed.Header.Equals("Exclude from shuffle"), "group toggle includes every selected track again");

            // Simulate device I/O still running. Both local clicks must apply
            // immediately and retain the most recent state while it waits.
            var deviceWrite = new TaskCompletionSource();
            var pendingValues = new List<bool>();
            var waiting = ShuffleMenu.Create([second], excluded => { pendingValues.Add(excluded); return deviceWrite.Task; });
            Click(waiting);
            Check(second.ExcludedFromShuffle && waiting.Header.Equals("Include in shuffle"), "pending device I/O does not block local exclusion");
            Click(waiting);
            Check(!second.ExcludedFromShuffle && waiting.Header.Equals("Exclude from shuffle"), "pending device I/O does not block toggling back");
            Check(pendingValues.SequenceEqual(new[] { true, false }), "rapid toggles keep the newest intent while a write is pending");
            deviceWrite.SetResult(); FlushBindings(icon);
            Check(!second.ExcludedFromShuffle, "completing an old write does not revert the model");

            track.ExcludedFromShuffle = true;
            var json = JsonSerializer.Serialize(new[] { track, second });
            var restored = JsonSerializer.Deserialize<Track[]>(json)!;
            Check(restored[0].ExcludedFromShuffle && !restored[1].ExcludedFromShuffle, "both preference states survive JSON persistence");
            Check(!ShuffleMenu.Create([], _ => Task.CompletedTask).IsEnabled, "empty selection cannot trigger a meaningless toggle");
            DownloadBrowseChecks.Run();
            Console.WriteLine($"All {_passed} WPF shuffle UI regression checks passed, plus download browser checks.");
            app.Shutdown();
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static Track NewTrack(string title) => new()
    { Title = title, Artist = "Artist", Album = "Album", Genre = "Rock", FileName = title + ".mp3" };
    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    private static string ColorOf(Path icon) => ((SolidColorBrush)icon.Stroke).Color.ToString();
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        _passed++; Console.WriteLine("PASS " + message);
    }
    private static void FlushBindings(FrameworkElement element)
    {
        element.Measure(new Size(30, 30)); element.Arrange(new Rect(0, 0, 30, 30)); element.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }
    private static ResourceDictionary ReadIconStyles()
    {
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var source = XDocument.Load(System.IO.Path.Combine(AppContext.BaseDirectory, "MainWindow.source.xaml"));
        var resources = source.Root!.Element(wpf + "Window.Resources")!;
        var root = new XElement(wpf + "ResourceDictionary", new XAttribute(XNamespace.Xmlns + "x", x));
        foreach (var resource in resources.Elements().Where(e => (string?)e.Attribute(x + "Key") is "ShuffleIcon" or "TrackShuffleIcon"))
            root.Add(new XElement(resource));
        return (ResourceDictionary)XamlReader.Parse(root.ToString());
    }
}
