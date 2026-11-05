using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Threading;
using Sink.Dialogs;
using Sink.Services.Download;

internal static class DownloadBrowseChecks
{
    private static int _passed;
    public static void Run()
    {
        var first = Album("First album");
        first.Children.Add(Track("Opening song", 1));
        first.Children.Add(Track("Second song", 2));
        var second = Album("Second album");
        var artist = new DownloadNode(DownloadKind.Artist) { Artist = "Example artist" };
        artist.Children.Add(first); artist.Children.Add(second);

        first.Children[0].Enabled = false;
        Check(artist.Enabled is null, "mixed tracks keep their artist partially selected");
        first.Enabled = true;
        using var popup = new PopupScope(new DownloadBrowseWindow("Example artist", true,
            _ => Task.FromResult(artist), (_, _) => Task.CompletedTask));
        popup.Window.LoadAsync().GetAwaiter().GetResult();
        Layout(popup.Window);
        var items = Find<ListBox>(popup.Window, "BrowseItems");
        Check(items.Items.Count == 2, "artist popup lists all albums");
        var albumBox = Descendants<CheckBox>(items).First();
        Click(albumBox);
        Check(first.Enabled == false && artist.Enabled is null, "album checkbox excludes the album and its tracks");
        Check(!Descendants<Button>(items).First(b => Equals(b.Tag, "Download")).IsEnabled,
            "unchecked albums disable their row download button");
        Check(DownloadSelection.PendingUnits(artist).SequenceEqual(new[] { second }), "excluded album is absent from the download set");
        Click(albumBox);
        Check(first.Enabled == true && artist.Enabled == true, "album checkbox includes all its tracks again");

        using var tracks = new PopupScope(new DownloadBrowseWindow(first.Name, false,
            _ => Task.FromResult(first), (_, _) => Task.CompletedTask));
        tracks.Window.LoadAsync().GetAwaiter().GetResult();
        Layout(tracks.Window);
        var trackItems = Find<ListBox>(tracks.Window, "BrowseItems");
        Check(trackItems.Items.Count == 2, "album popup lists every track");
        Click(Descendants<CheckBox>(trackItems).First());
        Check(first.Children[0].Enabled == false && first.Children[1].Enabled == true,
            "track checkbox excludes only the chosen track");
        Check(first.Enabled is null && artist.Enabled is null, "track selection propagates to album and artist checkboxes");
        second.Enabled = false;
        var snapshot = DownloadSelection.Snapshot(artist);
        var selected = DownloadSelection.PendingUnits(snapshot);
        Check(selected.Count == 1 && selected[0].Name == first.Name, "download contains only selected albums");
        Check(selected[0].Scanned && selected[0].Children.Count == 2
            && selected[0].Children[0].Enabled == false && selected[0].Children[1].Index == 2,
            "queue snapshot retains unchecked tracks and original source positions");
        selected[0].Children.Clear();
        Check(first.Children.Count == 2 && first.Children[0].Enabled == false,
            "queue pruning leaves catalog choices intact");

        var all = Find<CheckBox>(tracks.Window, "BrowseSelectAll");
        // The mixed state becomes checked on the first click; click again to clear.
        Click(all);
        if (first.Enabled != false) Click(all);
        Check(first.Children.All(n => n.Enabled == false)
            && !Find<Button>(tracks.Window, "BrowseDownload").IsEnabled,
            "clearing the selection disables Download selected");
        Click(all);
        Check(first.Children.All(n => n.Enabled == true)
            && Find<Button>(tracks.Window, "BrowseDownload").IsEnabled,
            "select all restores tracks and enables Download selected");

        using var empty = new PopupScope(new DownloadBrowseWindow("Empty album", false,
            _ => Task.FromResult(Album("Empty album")), (_, _) => Task.CompletedTask));
        empty.Window.LoadAsync().GetAwaiter().GetResult(); Layout(empty.Window);
        Check(!Find<Button>(empty.Window, "BrowseDownload").IsEnabled,
            "empty catalog results cannot start a download");

        using var failed = new PopupScope(new DownloadBrowseWindow("Unavailable album", false,
            _ => Task.FromException<DownloadNode>(new IOException("Test catalog unavailable")), (_, _) => Task.CompletedTask));
        failed.Window.LoadAsync().GetAwaiter().GetResult(); Layout(failed.Window);
        Check(Find<TextBlock>(failed.Window, "BrowseStatus").Text.Contains("Test catalog unavailable"),
            "catalog failures display a retryable error");

        CheckNestedDownload(artist, first, rightClick: false);
        CheckNestedDownload(artist, first, rightClick: true);

        // Render the real controls offscreen for layout inspection without
        // launching Sink or touching the user's library/device.
        Layout(popup.Window);
        var visual = (FrameworkElement)popup.Window.Content;
        var drawing = new DrawingVisual();
        using (var canvas = drawing.RenderOpen())
        {
            canvas.DrawRectangle(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#161A22")), null, new Rect(0, 0, 740, 660));
            canvas.DrawRectangle(new VisualBrush(visual), null, new Rect(26, 24, visual.ActualWidth, visual.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap(740, 660, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(System.IO.Path.Combine(AppContext.BaseDirectory, "download-browser-preview.png"));
        encoder.Save(output);
        Console.WriteLine($"All {_passed} download browser interaction checks passed.");
    }

    private static void CheckNestedDownload(DownloadNode artist, DownloadNode first, bool rightClick)
    {
        first.Enabled = true;
        using var scope = new PopupScope(new DownloadBrowseWindow("Example artist", true,
            _ => Task.FromResult(artist), (_, _) => Task.CompletedTask));
        var root = scope.Window;
        root.WindowStartupLocation = WindowStartupLocation.Manual;
        root.Left = -10000; root.Top = -10000; root.ShowActivated = false;
        Exception? failure = null;
        root.Loaded += (_, _) => Dispatcher.CurrentDispatcher.BeginInvoke(() =>
        {
            try
            {
                var rows = Find<ListBox>(root, "BrowseItems");
                var title = Descendants<Button>(rows).First(b => Equals(b.Tag, "Browse"));
                Dispatcher.CurrentDispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        var child = Application.Current.Windows.OfType<DownloadBrowseWindow>().Single(w => w.Owner == root);
                        var tracks = Find<ListBox>(child, "BrowseItems");
                        Check(tracks.Items.Count == first.Children.Count, rightClick
                            ? "right-clicking an artist album row opens its real track popup"
                            : "clicking an artist album title opens its real track popup");
                        Click(Descendants<CheckBox>(tracks).First());
                        Find<Button>(child, "BrowseDownload").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                        foreach (var child in Application.Current.Windows.OfType<DownloadBrowseWindow>().Where(w => w.Owner == root).ToArray()) child.Close();
                        root.Close();
                    }
                }, DispatcherPriority.ApplicationIdle);
                if (rightClick)
                {
                    var row = (ListBoxItem)rows.ItemContainerGenerator.ContainerFromIndex(0);
                    rows.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                    { RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent, Source = row });
                }
                else title.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }
            catch (Exception ex) { failure = ex; root.Close(); }
        }, DispatcherPriority.ApplicationIdle);
        var result = root.ShowDialog();
        if (failure is not null) throw failure;
        Check(result == true && ReferenceEquals(root.RequestedDownload, first),
            "nested Download selected returns the chosen album through the artist popup");
        Check(first.Children[0].Enabled == false && first.Children[1].Enabled == true,
            "nested download retains the track exclusions chosen in the child popup");
    }

    private static DownloadNode Album(string name) => new(DownloadKind.Album)
    { Album = name, Artist = "Example artist", Url = "https://example.test/" + name, Scanned = true };
    private static DownloadNode Track(string title, int index) => new(DownloadKind.Track)
    { Title = title, Index = index, Url = "https://example.test/track/" + index };
    private static void Check(bool value, string description)
    {
        if (!value) throw new Exception(description);
        _passed++; Console.WriteLine("PASS " + description);
    }
    private static void Click(CheckBox box)
    {
        // Invoke the actual ToggleButton click (RaiseEvent alone does not toggle it).
        typeof(ToggleButton).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(box, null);
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }
    private static void Layout(DownloadBrowseWindow window)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(740, 660)); content.Arrange(new Rect(0, 0, 740, 660)); content.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }
    private static T Find<T>(DownloadBrowseWindow window, string name) where T : FrameworkElement =>
        Descendants<T>((DependencyObject)window.Content).First(e => e.Name == name);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private sealed class PopupScope(DownloadBrowseWindow window) : IDisposable
    {
        public DownloadBrowseWindow Window => window;
        public void Dispose() => window.Close();
    }
}
