using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Sink.Dialogs;
using Sink.Services;
using Sink.Services.Download;

namespace Sink;

/// <summary>
/// The Download page: paste YouTube links, scan them into an artist → album →
/// track tree, tick what you want, then hand it to yt-dlp and import the results.
/// Double-clicking a track title previews it without saving anything.
/// </summary>
public partial class MainWindow
{
    private readonly ObservableCollection<DownloadNode> _rootNodes = [];
    private bool _downloadViewActive;
    private bool _toolsChecked;
    private bool _downloading;
    private CancellationTokenSource? _downloadCts;

    private void InitDownloadPage()
    {
        LinksTree.ItemsSource = _rootNodes;
        _rootNodes.CollectionChanged += (_, _) => RefreshDownloadChrome();
        _previewPlayer.MediaEnded += (_, _) => StopPreview("finished");
        foreach (var node in DownloadQueueStore.Load()) _rootNodes.Add(node);
        RefreshDownloadChrome();
    }

    // ---- Per-row "busy" spinner --------------------------------------
    //
    // Used to be a XAML DataTrigger + Storyboard with RepeatBehavior=Forever
    // inside the (virtualized) tree's HierarchicalDataTemplate. Switching
    // that to a code-behind BeginAnimation wasn't enough on its own — the
    // <RotateTransform/> declared inline in the template is itself sealed by
    // WPF's template-sharing/virtualization machinery, so animating it
    // (Storyboard or BeginAnimation, doesn't matter) throws "Cannot animate
    // the 'Angle' property ... because the object is sealed or frozen" every
    // time a recycled container gets Loaded. Never touch the template's own
    // transform: assign a fresh, definitely-unfrozen RotateTransform we
    // created ourselves the moment the row loads, and animate that instead.
    private void TrackSpinner_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DownloadNode node } grid) return;
        grid.RenderTransform = new RotateTransform();
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName is nameof(DownloadNode.IsBusy) or null) UpdateTrackSpinner(grid, node);
        };
        grid.Tag = handler;
        node.PropertyChanged += handler;
        UpdateTrackSpinner(grid, node);
    }

    private void TrackSpinner_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DownloadNode node } grid) return;
        if (grid.Tag is PropertyChangedEventHandler handler) node.PropertyChanged -= handler;
        // Safe to stop here (unlike the template's own transform): Loaded
        // always replaces RenderTransform with one of our own instances
        // before this could ever fire, so it's never the frozen one — and
        // stopping it means a RepeatBehavior.Forever clock isn't left
        // ticking in the background for a container that's just been
        // recycled away.
        if (grid.RenderTransform is RotateTransform rt) rt.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    private static void UpdateTrackSpinner(FrameworkElement grid, DownloadNode node)
    {
        grid.Visibility = node.IsBusy ? Visibility.Visible : Visibility.Collapsed;
        if (grid.RenderTransform is not RotateTransform rt) return;
        rt.BeginAnimation(RotateTransform.AngleProperty, node.IsBusy
            ? new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever }
            : null);
    }

    /// <summary>
    /// Persists every root-level link that hasn't finished successfully —
    /// failed, or still mid-download — so it survives a restart (task 135).
    /// Only covered explicitly-failed links before; a crash mid-download
    /// (see task 149) killed the process before a node ever reached
    /// DownloadState.Failed, so nothing about it was ever saved and the user
    /// had to re-paste the same links after every crash. Called after each
    /// item finishes in StartDownloads_Click, not just once at the very end,
    /// so an abrupt crash only loses ground back to the last-completed item
    /// rather than the whole run.
    /// </summary>
    private void SaveFailedDownloadQueue() =>
        DownloadQueueStore.Save(_rootNodes.Where(n => n.State != DownloadState.Done));

    // ---- View switching -------------------------------------------------

    private void ShowDownloadSource_Click(object sender, RoutedEventArgs e)
    {
        ExitPodcastView(); // was left showing underneath — task 122
        _downloadViewActive = true;
        MusicPage.Visibility = Visibility.Collapsed;
        DownloadPage.Visibility = Visibility.Visible;
        IpodCanvas.Visibility = Visibility.Collapsed;

        MusicNav.Visibility = Visibility.Collapsed;
        IpodNav.Visibility = Visibility.Collapsed;
        MusicHeaderButton.Tag = null;
        IpodHeaderButton.Tag = null;
        DownloadHeaderButton.Tag = "Active";
        SetActiveNavigation(null);

        EnsureToolsReady();
    }

    private void ExitDownloadView()
    {
        if (!_downloadViewActive) return;
        _downloadViewActive = false;
        StopPreview("left the page");
        DownloadPage.Visibility = Visibility.Collapsed;
        MusicPage.Visibility = Visibility.Visible;
        IpodCanvas.Visibility = Visibility.Visible;
        DownloadHeaderButton.Tag = null;
    }

    // ---- Tool bootstrap ----------------------------------------------------

    private async void EnsureToolsReady()
    {
        if (_toolsChecked && ToolManager.ToolsPresent) return;
        _toolsChecked = true;
        var progress = new Progress<string>(SetDownloadStatus);
        SetDownloadStatus("Checking yt-dlp and ffmpeg…");
        try
        {
            await Task.Run(() => ToolManager.EnsureAsync(progress));
        }
        catch (Exception ex)
        {
            SetDownloadStatus($"Tool setup failed: {ex.Message}");
            _toolsChecked = false;
        }
        UpdateDownloadButtonState();
    }

    // ---- Adding + scanning links ---------------------------------------

    private async void AddLink_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddLinkWindow { Owner = this };
        BlurBehind(true);
        var ok = dialog.ShowDialog() == true;
        BlurBehind(false);
        if (!ok) return;
        // One at a time — each scan already reports its own status line, and
        // running yt-dlp processes concurrently for a whole pasted batch would
        // just contend with itself for no benefit (task 152).
        foreach (var link in dialog.Links) await ScanAndAddAsync(link);
    }

    private async Task ScanAndAddAsync(string url)
    {
        var probe = new DownloadNode(DownloadKind.Single)
        {
            Url = url,
            Title = url,
            State = DownloadState.Scanning,
            StatusText = "Scanning…",
        };
        _rootNodes.Add(probe);

        try
        {
            var info = await Task.Run(() => DownloadService.ScanAsync(url));
            var at = Math.Max(0, _rootNodes.IndexOf(probe));
            _rootNodes.Remove(probe);

            var node = BuildNode(url, info);
            _rootNodes.Insert(Math.Min(at, _rootNodes.Count), node);

            if (node.Kind == DownloadKind.Artist)
            {
                SetDownloadStatus($"{node.Artist}: {node.Children.Count} albums — expand one to load its tracks");
            }
            else if (node.Kind == DownloadKind.Album)
            {
                node.StatusText = $"Album · {node.Children.Count} tracks";
                node.Scanned = true;
            }
            else
            {
                node.StatusText = "Ready";
            }
            SaveFailedDownloadQueue(); // a pasted-and-scanned link is durable even if never downloaded yet (task 149)
        }
        catch (Exception ex)
        {
            probe.State = DownloadState.Failed;
            probe.StatusText = $"Couldn't scan — {Shorten(ex.Message)}";
            Log.Error($"yt-dlp scan failed for {url}", ex);
            SetDownloadStatus($"Scan failed for {url}: {ex.Message} (see Settings ▸ Open logs)");
            SaveFailedDownloadQueue();
        }
        UpdateDownloadButtonState();
    }

    private static DownloadNode BuildNode(string url, ScannedInfo info)
    {
        if (info.Albums is { Count: > 0 })
        {
            var artist = new DownloadNode(DownloadKind.Artist)
            {
                Url = url,
                Artist = info.Artist,
                Genre = "Unknown",
                State = DownloadState.Ready,
                StatusText = $"{info.Albums.Count} albums",
            };
            foreach (var album in info.Albums)
            {
                artist.Children.Add(new DownloadNode(DownloadKind.Album)
                {
                    Url = album.Url,
                    Album = album.Title,
                    Artist = info.Artist,
                    Genre = "Unknown",
                    State = DownloadState.Ready,
                    StatusText = "Expand to load tracks",
                });
            }
            return artist;
        }

        if (info.IsPlaylist)
        {
            // A directly-pasted playlist link (as opposed to an album reached
            // by expanding an artist) commonly mixes tracks from different
            // artists/albums — don't force every track to share one Album tag
            // just because they happened to be collected into this playlist
            // (task 151). The container itself still keeps a name/artist for
            // display purposes; only per-track tagging skips the shared value.
            var node = new DownloadNode(DownloadKind.Album)
            {
                Url = url,
                Album = info.Album,
                Artist = info.Artist,
                Genre = info.Genre,
                State = DownloadState.Ready,
                IsMixedPlaylist = true,
            };
            FillTracks(node, info);
            return node;
        }

        return new DownloadNode(DownloadKind.Single)
        {
            Url = url,
            Title = info.Title,
            Artist = info.Artist,
            Album = info.Album,
            Genre = info.Genre,
            State = DownloadState.Ready,
        };
    }

    private static void FillTracks(DownloadNode album, ScannedInfo info)
    {
        album.Children.Clear();
        if (info.TrackTitles is not { Count: > 0 }) return;
        for (var i = 0; i < info.TrackTitles.Count; i++)
        {
            var title = info.TrackTitles[i];
            // Per-track artist defaults from each entry's own uploader/channel
            // for a mixed playlist — editable from there, never forced (task 151).
            var trackArtist = album.IsMixedPlaylist && info.TrackArtists is { } artists && i < artists.Count
                ? artists[i] : "";
            album.Children.Add(new DownloadNode(DownloadKind.Track)
            {
                Index = i + 1,
                Title = title,
                ScannedTitle = title,
                Artist = trackArtist,
                State = DownloadState.Ready,
                StatusText = "",
            });
        }
    }

    /// <summary>Loads an album's track list the first time it is expanded.</summary>
    private void LinksTree_ItemExpanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { DataContext: DownloadNode { Kind: DownloadKind.Album, Scanned: false } node }
            && node.State != DownloadState.Scanning)
            _ = ScanAlbumTracksAsync(node);
    }

    private async Task ScanAlbumTracksAsync(DownloadNode node)
    {
        node.State = DownloadState.Scanning;
        node.StatusText = "Loading tracks…";
        try
        {
            var info = await Task.Run(() => DownloadService.ScanAsync(node.Url));
            FillTracks(node, info);
            if (!string.IsNullOrWhiteSpace(info.Artist) && node.Artist is "" or "Unknown Artist")
                node.Artist = info.Artist;
            node.Scanned = true;
            node.State = DownloadState.Ready;
            RefreshDownloadNodeStatus(node);
        }
        catch (Exception ex)
        {
            node.State = DownloadState.Failed;
            node.StatusText = $"Couldn't load tracks — {Shorten(ex.Message)}";
            Log.Error($"track scan failed for {node.Url}", ex);
            SetDownloadStatus($"Couldn't load tracks for {node.Name}: {ex.Message} (see Settings ▸ Open logs)");
        }
        UpdateDownloadButtonState();
    }

    private static void RefreshDownloadNodeStatus(DownloadNode node)
    {
        if (node.State is DownloadState.Downloading or DownloadState.Importing or DownloadState.Done) return;
        var art = node.HasArtworkOverride ? " · custom art" : "";
        node.StatusText = node.Kind switch
        {
            DownloadKind.Artist => $"{node.Children.Count} albums",
            DownloadKind.Album => (node.Scanned ? $"{node.Children.Count} tracks" : "Expand to load tracks") + art,
            DownloadKind.Single => "Ready" + art,
            _ => node.StatusText,
        };
    }

    private void SetNodeArtwork(DownloadNode node)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose cover art",
            Filter = "Images|*.jpg;*.jpeg;*.png;*.webp;*.bmp|All files|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        node.ArtworkOverride = dialog.FileName;
        RefreshDownloadNodeStatus(node);
        SetDownloadStatus($"Cover art set for “{node.Name}”");
    }

    /// <summary>
    /// Right-click → translate: rewrites the node's title (and, for an album or
    /// artist, its children's titles) to English. Text that is already English
    /// is left alone. Runs off the UI thread via <see cref="Translation"/>'s
    /// async HTTP call.
    /// </summary>
    private async Task TranslateNodeAsync(DownloadNode node)
    {
        var targets = new List<DownloadNode> { node };
        if (node.Kind is DownloadKind.Album or DownloadKind.Artist)
            targets.AddRange(node.Children);
        if (node.Kind == DownloadKind.Artist)
            targets.AddRange(node.Children.SelectMany(c => c.Children));
        targets = targets.Where(t => !string.IsNullOrWhiteSpace(t.Name)).Distinct().ToList();
        if (targets.Count == 0) return;

        // Mark every row up front so the whole set spins, then translate one at
        // a time with a short gap between calls. The keyless endpoint throttles
        // bursts hard, which is what left half an album untranslated (task 120).
        foreach (var target in targets) target.Translating = true;
        SetDownloadStatus(targets.Count > 1 ? $"Translating {targets.Count} titles…" : "Translating…");

        var changed = 0;
        var failed = 0;
        var pending = new List<DownloadNode>();
        for (var i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            try
            {
                var result = await Translation.ToEnglishAsync(target.Name);
                if (!result.Ok) { failed++; pending.Add(target); }
                else if (result.Changed) { target.Name = result.Text; changed++; }
            }
            catch (Exception ex)
            {
                failed++;
                pending.Add(target);
                Log.Warn($"Translate failed for \"{target.Name}\": {ex.Message}");
            }
            finally
            {
                target.Translating = false;
            }
            if (targets.Count > 1) SetDownloadStatus($"Translating… {i + 1}/{targets.Count}");
            if (i < targets.Count - 1) await Task.Delay(150);
        }

        // One more pass over the ones the service refused — usually a transient rate-limit.
        if (pending.Count > 0)
        {
            SetDownloadStatus($"Retrying {pending.Count} title{(pending.Count == 1 ? "" : "s")}…");
            foreach (var target in pending)
            {
                await Task.Delay(500);
                target.Translating = true;
                try
                {
                    var result = await Translation.ToEnglishAsync(target.Name);
                    if (result.Ok) { failed--; if (result.Changed) { target.Name = result.Text; changed++; } }
                }
                catch (Exception ex) { Log.Warn($"Translate retry failed for \"{target.Name}\": {ex.Message}"); }
                finally { target.Translating = false; }
            }
        }

        var message = changed == 0
            ? (failed == 0 ? "Titles are already English" : "Couldn't reach the translation service")
            : $"Translated {changed} title{(changed == 1 ? "" : "s")} to English";
        if (failed > 0 && changed > 0) message += $" · {failed} still failed — try again";
        SetDownloadStatus(message);
    }

    private void EditNodeMetadata(DownloadNode node)
    {
        var dialog = new DownloadMetadataWindow(node) { Owner = this };
        BlurBehind(true);
        var ok = dialog.ShowDialog() == true;
        BlurBehind(false);
        if (!ok) return;
        RefreshDownloadNodeStatus(node);
        SetDownloadStatus($"Updated metadata for “{node.Name}”");
    }

    /// <summary>
    /// Sets every track's number to its position in the list (task 109). Works
    /// from the album, artist or an individual track's menu (task 113); loads a
    /// collapsed album's tracks first so the option is never a no-op.
    /// </summary>
    private async void NumberTracksByOrder(DownloadNode node)
    {
        try
        {
            foreach (var album in node.SelfAndDescendants().Where(n => n.Kind == DownloadKind.Album && !n.Scanned).ToList())
            {
                album.IsExpanded = true;
                await ScanAlbumTracksAsync(album);
            }
            var tracks = node.SelfAndDescendants().Where(c => c.Kind == DownloadKind.Track).ToList();
            foreach (var track in tracks)
                if (track.Index > 0) track.TrackNumber = track.Index;
            SetDownloadStatus(tracks.Count == 0
                ? "No tracks to number yet"
                : $"Numbered {tracks.Count} track{(tracks.Count == 1 ? "" : "s")} by list order");
        }
        catch (Exception ex)
        {
            Log.Error("Numbering tracks failed", ex);
            SetDownloadStatus($"Couldn't number tracks: {ex.Message}");
        }
    }

    private void TrimTrackTitles(DownloadNode album)
    {
        var tracks = album.Children.Where(c => c.Kind == DownloadKind.Track).ToList();
        if (tracks.Count == 0) return;
        var dialog = new TrimTitlesWindow(tracks) { Owner = this };
        BlurBehind(true);
        var ok = dialog.ShowDialog() == true;
        BlurBehind(false);
        if (ok) SetDownloadStatus($"Trimmed {tracks.Count} track title{(tracks.Count == 1 ? "" : "s")}");
    }

    private void RemoveNode_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadNode node) return;
        if (node.Parent is null) _rootNodes.Remove(node);
        else node.Parent.Children.Remove(node);
        if (_previewNode is not null && !_rootNodes.SelectMany(n => n.SelfAndDescendants()).Contains(_previewNode))
            StopPreview("removed");
        SaveFailedDownloadQueue();
    }

    // ---- Arrow-key navigation between the inline metadata fields (task 104) ----
    //
    // Down / Up move to the same column of the next / previous visible row;
    // Right / Left step to the next / previous field on the same row, but only
    // once the caret has reached that edge of the text. A column that a row
    // doesn't have (an expanded album's Genre → its first track) lands on that
    // row's name field, so the progression flows straight down the tree.

    private static readonly string[] FieldColumns = ["Name", "Track", "Artist", "Album", "Genre"];

    private void NodeField_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled) return; // an open autocomplete popup already used the arrow
        if (sender is not TextBox box || box.DataContext is not DownloadNode node) return;
        if (e.Key is not (Key.Down or Key.Up or Key.Left or Key.Right)) return;
        var column = box.Tag as string ?? "Name";

        if (e.Key is Key.Left or Key.Right && box.SelectionLength > 0) return;
        if (e.Key == Key.Right && box.CaretIndex < box.Text.Length) return;
        if (e.Key == Key.Left && box.CaretIndex > 0) return;

        var visible = VisibleNodes().ToList();
        var row = visible.IndexOf(node);
        if (row < 0) return;

        DownloadNode? targetNode = null;
        string targetColumn = column;
        switch (e.Key)
        {
            case Key.Down when row + 1 < visible.Count: targetNode = visible[row + 1]; break;
            case Key.Up when row > 0: targetNode = visible[row - 1]; break;
            case Key.Right:
                targetColumn = NextColumn(node, column, 1) ?? column;
                if (targetColumn != column) targetNode = node;
                else if (row + 1 < visible.Count) { targetNode = visible[row + 1]; targetColumn = "Name"; }
                break;
            case Key.Left:
                targetColumn = NextColumn(node, column, -1) ?? column;
                if (targetColumn != column) targetNode = node;
                else if (row > 0) { targetNode = visible[row - 1]; targetColumn = LastColumn(visible[row - 1]); }
                break;
        }
        if (targetNode is null) return;
        if (!ColumnVisible(targetNode, targetColumn)) targetColumn = "Name";

        e.Handled = true;
        FocusNodeField(targetNode, targetColumn);
    }

    private IEnumerable<DownloadNode> VisibleNodes()
    {
        IEnumerable<DownloadNode> Walk(DownloadNode n)
        {
            yield return n;
            if (!n.IsExpanded) yield break;
            foreach (var child in n.Children)
                foreach (var d in Walk(child))
                    yield return d;
        }
        return _rootNodes.SelectMany(Walk);
    }

    private static bool ColumnVisible(DownloadNode node, string column) => column switch
    {
        "Name" => true,
        "Track" => node.ShowTrackNumber,
        "Artist" => node.ShowArtist,
        "Album" => node.ShowAlbum,
        "Genre" => node.ShowGenre,
        _ => false,
    };

    private static string? NextColumn(DownloadNode node, string from, int direction)
    {
        var order = FieldColumns.Where(c => ColumnVisible(node, c)).ToList();
        var i = order.IndexOf(from);
        if (i < 0) return null;
        var j = i + direction;
        return j >= 0 && j < order.Count ? order[j] : null;
    }

    private static string LastColumn(DownloadNode node) =>
        FieldColumns.Where(c => ColumnVisible(node, c)).LastOrDefault() ?? "Name";

    private void FocusNodeField(DownloadNode node, string column)
    {
        var container = ContainerFromNode(LinksTree, node);
        if (container is null) return;
        container.BringIntoView();
        Dispatcher.BeginInvoke(() =>
        {
            var field = FindField(container, column) ?? FindField(container, "Name");
            if (field is null) return;
            field.Focus();
            field.SelectAll();
        }, DispatcherPriority.Input);
    }

    private static TreeViewItem? ContainerFromNode(ItemsControl parent, DownloadNode node)
    {
        for (var i = 0; i < parent.Items.Count; i++)
        {
            if (parent.ItemContainerGenerator.ContainerFromIndex(i) is not TreeViewItem item) continue;
            if (ReferenceEquals(parent.Items[i], node)) return item;
            if (ContainerFromNode(item, node) is { } nested) return nested;
        }
        return null;
    }

    private static TextBox? FindField(DependencyObject root, string column)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TreeViewItem) continue; // stay within this row
            if (child is TextBox box && box.Tag as string == column) return box;
            if (FindField(child, column) is { } found) return found;
        }
        return null;
    }

    // ---- Right-click menu ----------------------------------------------

    // A TreeView doesn't select on right-click, so the context menu was acting
    // on whatever row was previously selected — you'd hit "Translate" on one
    // track and watch a different row spin. Select the row under the cursor
    // first (task 120).
    private void LinksTree_PreviewRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is TreeViewItem item)
            {
                item.IsSelected = true;
                item.Focus();
                break;
            }
    }

    private void LinksTree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = LinksTree.ContextMenu!;
        menu.Items.Clear();
        if (LinksTree.SelectedItem is not DownloadNode node) { e.Handled = true; return; }

        menu.Items.Add(Header(string.IsNullOrWhiteSpace(node.Name) ? node.Url : node.Name));
        menu.Items.Add(new Separator());
        if (node.Kind is DownloadKind.Track or DownloadKind.Single)
            menu.Items.Add(Item("Preview", () => _ = PreviewNodeAsync(node)));
        menu.Items.Add(Item("Edit metadata…", () => EditNodeMetadata(node)));
        menu.Items.Add(Item(
            node.Kind == DownloadKind.Artist ? "Translate names to English"
            : node.CanHaveChildren ? "Translate titles to English"
            : "Translate title to English",
            () => _ = TranslateNodeAsync(node)));
        if (node.CanHaveChildren)
        {
            menu.Items.Add(Item("Select all", () => node.Enabled = true));
            menu.Items.Add(Item("Select none", () => node.Enabled = false));
        }
        if (node.Kind is DownloadKind.Album or DownloadKind.Single)
            menu.Items.Add(Item(node.HasArtworkOverride ? "Change cover art…" : "Set cover art…", () => SetNodeArtwork(node)));
        if (node.HasArtworkOverride)
            menu.Items.Add(Item("Clear cover art", () => { node.ArtworkOverride = null; RefreshDownloadNodeStatus(node); }));
        if (node.Kind == DownloadKind.Album && node.Children.Any(c => c.Kind == DownloadKind.Track))
            menu.Items.Add(Item("Trim track titles…", () => TrimTrackTitles(node)));
        if (node.Kind is DownloadKind.Album or DownloadKind.Artist)
            menu.Items.Add(Item("Number tracks by list order", () => NumberTracksByOrder(node)));
        else if (node.Kind == DownloadKind.Track && node.Parent is not null)
            menu.Items.Add(Item("Number tracks by list order", () => NumberTracksByOrder(node.Parent)));
        if (node.Kind == DownloadKind.Album)
            menu.Items.Add(Item("Rescan tracks", () => { node.Scanned = false; node.IsExpanded = true; _ = ScanAlbumTracksAsync(node); }));
        menu.Items.Add(Item("Copy link", () =>
        {
            try { Clipboard.SetText(node.Url); }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or OutOfMemoryException) { }
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Remove", () =>
        {
            if (node.Parent is null) _rootNodes.Remove(node);
            else node.Parent.Children.Remove(node);
        }));
    }

    /// <summary>Clicking a row's status light toggles it between "will download" and "skip" (task 110).</summary>
    private void StatusGlyph_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadNode node) return;
        e.Handled = true;
        if (node.State is DownloadState.Done or DownloadState.Downloading or DownloadState.Importing) return;
        node.Enabled = node.Enabled != true;
        UpdateDownloadButtonState();
    }

    private void LinksTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_previewNode is not null && !ReferenceEquals(e.NewValue, _previewNode))
            StopPreview("changed selection");
    }

    // ---- Options --------------------------------------------------------

    private DownloadOptions ReadOptions() => new()
    {
        WriteMetadata = OptMetadata.IsChecked == true,
        EmbedAlbumArt = OptAlbumArt.IsChecked == true,
        PreferMusicMetadata = OptMusicMeta.IsChecked == true,
        NumberTracks = OptNumberTracks.IsChecked == true,
        Format = OptFormat.SelectedIndex switch
        {
            1 => AudioFormat.M4a,
            2 => AudioFormat.Opus,
            3 => AudioFormat.Flac,
            4 => AudioFormat.Wav,
            _ => AudioFormat.Mp3
        },
        Quality = (OptQuality.SelectedItem as ComboBoxItem)?.Tag is string tag && int.TryParse(tag, out var q) ? q : 0,
    };

    // ---- Preview (double-click a track) -------------------------------

    private readonly MediaPlayer _previewPlayer = new();
    private DownloadNode? _previewNode;
    private string? _previewFile;
    private CancellationTokenSource? _previewCts;

    // A TextBox swallows MouseLeftButtonDown for caret placement, which stops
    // Control.MouseDoubleClick / PreviewMouseDoubleClick from ever firing — so
    // detect the double-click straight off the tunnelling button-down instead.
    private void NodeName_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if ((sender as FrameworkElement)?.DataContext is not DownloadNode node) return;
        if (node.Kind is not (DownloadKind.Track or DownloadKind.Single)) return;
        e.Handled = true;
        _ = PreviewNodeAsync(node);
    }

    private async Task PreviewNodeAsync(DownloadNode node)
    {
        StopPreview("new preview");
        if (!ToolManager.ToolsPresent) { SetDownloadStatus("Still setting up yt-dlp…"); EnsureToolsReady(); return; }

        _previewNode = node;
        _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;
        var (sourceUrl, index) = node.Kind == DownloadKind.Track
            ? (node.Parent?.Url ?? node.Url, node.Index)
            : (node.Url, 0);

        node.StatusText = "Loading preview…";
        SetDownloadStatus($"Preview: fetching “{node.Name}”…");
        try
        {
            var file = await DownloadService.PreviewTrackAsync(
                sourceUrl, index, ReadOptions(), new Progress<string>(SetDownloadStatus), token);
            if (token.IsCancellationRequested || !ReferenceEquals(_previewNode, node))
            {
                TryDelete(file);
                return;
            }

            StopLibraryPlaybackForPreview();
            _previewFile = file;
            _previewPlayer.Open(new Uri(file));
            _previewPlayer.Volume = VolumeSlider?.Value ?? 0.7;
            _previewPlayer.Play();
            _playbackTimer.Start();

            node.StatusText = "Previewing…";
            PlayerTitle.Text = node.Name;
            PlayerArtist.Text = "Preview — not saved unless you download it";
            PlayerArtInitial.Text = "▶";
            SetNowPlayingArt(null);
            PlayPauseButton.Content = "Ⅱ";
            SetDownloadStatus($"Previewing “{node.Name}”");

            // Pull the cover out of the just-downloaded preview file (task 119).
            var artKey = "preview:" + node.Url + ":" + node.Index;
            var artPath = await Task.Run(() => Artwork.ExtractAndCrop(file, artKey));
            if (artPath is not null && ReferenceEquals(_previewNode, node))
            {
                SetNowPlayingArt(artPath);
                PlayerArtInitial.Text = node.Name is { Length: > 0 } name
                    ? name[..1].ToUpperInvariant() : "▶";
            }
        }
        catch (OperationCanceledException)
        {
            node.StatusText = node.Kind == DownloadKind.Track ? "" : "Ready";
        }
        catch (Exception ex)
        {
            node.StatusText = "Preview failed";
            SetDownloadStatus($"Preview failed: {ex.Message}");
            Log.Error("Preview failed", ex);
        }
    }

    private void StopLibraryPlaybackForPreview()
    {
        if (_playingEpisode is not null) StopPodcast(markPlayed: false);
        if (_nowPlaying is not null)
        {
            _mediaPlayer.Stop();
            _isPlaying = false;
            _nowPlaying = null;
        }
        UpdateRecordSpin();
    }

    private void StopPreview(string why)
    {
        var node = _previewNode;
        if (node is null && _previewFile is null && _previewCts is null) return;

        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = null;
        _previewNode = null;

        try { _previewPlayer.Stop(); _previewPlayer.Close(); } catch (Exception e) when (e is not OutOfMemoryException) { }
        if (_previewFile is not null) { TryDelete(_previewFile); _previewFile = null; }
        DownloadService.ClearPreviews();

        if (node is not null && node.StatusText is "Previewing…" or "Loading preview…")
            node.StatusText = node.Kind == DownloadKind.Track ? "" : "Ready";

        if (_nowPlaying is null && _playingEpisode is null)
        {
            _playbackTimer.Stop();
            PlayPauseButton.Content = "▶";
            PlayerTitle.Text = "Choose something to play";
            PlayerArtist.Text = "Your library is ready";
            PlayerArtInitial.Text = "♫";
            _updatingProgress = true;
            ProgressSlider.Value = 0;
            _updatingProgress = false;
            ElapsedText.Text = "0:00";
            RemainingText.Text = "-0:00";
        }
        UpdateRecordSpin();
    }

    private bool PreviewActive => _previewNode is not null && _previewFile is not null;

    private void TogglePreviewPause()
    {
        if (_previewPlayer.NaturalDuration.HasTimeSpan && _previewPlayer.Position >= _previewPlayer.NaturalDuration.TimeSpan)
            return;
        if (PlayPauseButton.Content as string == "Ⅱ")
        {
            _previewPlayer.Pause();
            PlayPauseButton.Content = "▶";
        }
        else
        {
            _previewPlayer.Play();
            PlayPauseButton.Content = "Ⅱ";
        }
    }

    private void UpdatePreviewProgress()
    {
        if (!_previewPlayer.NaturalDuration.HasTimeSpan) return;
        var dur = _previewPlayer.NaturalDuration.TimeSpan;
        _updatingProgress = true;
        ProgressSlider.Maximum = Math.Max(1, dur.TotalSeconds);
        ProgressSlider.Value = Math.Min(dur.TotalSeconds, _previewPlayer.Position.TotalSeconds);
        _updatingProgress = false;
        ElapsedText.Text = FormatTime(_previewPlayer.Position);
        RemainingText.Text = $"-{FormatTime(dur - _previewPlayer.Position)}";
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // ---- Download run ---------------------------------------------------

    private List<DownloadNode> DownloadUnits() => _rootNodes
        .SelectMany(n => n.SelfAndDescendants())
        .Where(n => n.Kind is DownloadKind.Album or DownloadKind.Single)
        .ToList();

    private async void StartDownloads_Click(object sender, RoutedEventArgs e)
    {
        if (_downloading)
        {
            _downloadCts?.Cancel();
            return;
        }

        var queue = DownloadUnits()
            .Where(n => n.State is not DownloadState.Done && n.Enabled != false)
            .Where(n => n.Kind == DownloadKind.Single
                        || n.Children.Count == 0
                        || n.Children.Any(c => c.Enabled == true))
            .ToList();
        if (queue.Count == 0) { SetDownloadStatus("Nothing ticked to download"); return; }
        if (!ToolManager.ToolsPresent) { SetDownloadStatus("Still setting up yt-dlp…"); EnsureToolsReady(); return; }

        StopPreview("starting download");
        var options = ReadOptions();
        _downloading = true;
        _downloadCts = new CancellationTokenSource();
        var token = _downloadCts.Token;
        DownloadButton.Content = "Stop";
        SpinIndicator(DownloadSpinner, true);
        var imported = 0;

        try
        {
            for (var index = 0; index < queue.Count && !token.IsCancellationRequested; index++)
            {
                var node = queue[index];
                node.State = DownloadState.Downloading;
                node.Progress = 0;
                node.StatusText = "Starting…";
                var linkLabel = queue.Count > 1 ? $"  (item {index + 1}/{queue.Count})" : "";

                try
                {
                    var progress = new Progress<double>(p =>
                    {
                        node.Progress = p;
                        node.StatusText = $"Downloading {p * 100:0}%";
                        UpdateAggregateProgress(queue);
                    });
                    var status = new Progress<string>(s => SetDownloadStatus(s + linkLabel));

                    // Relocate (copy/move) and add each track the moment it's
                    // finalized, instead of waiting for the whole album to
                    // finish and flushing it all at once (task 121).
                    var alreadyImported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var pendingImports = new List<Task<int>>();
                    var fileReady = new Progress<string>(path =>
                    {
                        if (alreadyImported.Add(path)) pendingImports.Add(ImportFinalizedFileAsync(path));
                    });

                    IReadOnlyList<string> paths;
                    var partialFailure = false;
                    var partialReason = "";
                    try
                    {
                        paths = await DownloadService.DownloadAsync(node, options, progress, status, fileReady, token);
                    }
                    catch (DownloadService.PartialDownloadException partial)
                    {
                        paths = partial.Downloaded;
                        partialFailure = true;
                        partialReason = partial.Reason;
                    }

                    node.State = DownloadState.Importing;
                    node.StatusText = "Importing…";
                    var progressiveCounts = await Task.WhenAll(pendingImports);
                    var remainder = paths.Where(p => alreadyImported.Add(p)).ToList(); // safety net
                    var tracks = remainder.Count > 0 ? await Task.Run(() => MusicImporter.Import(remainder)) : [];
                    foreach (var track in tracks) _tracks.Add(track);
                    imported += progressiveCounts.Sum() + tracks.Count;

                    if (partialFailure)
                    {
                        node.State = DownloadState.Failed;
                        var missing = node.Children.Count(c => c.Kind == DownloadKind.Track && c.State == DownloadState.Failed);
                        node.StatusText = $"{tracks.Count} done, {missing} failed — {Shorten(partialReason)}";
                        SetDownloadStatus($"{node.Name}: {missing} track{(missing == 1 ? "" : "s")} failed — {partialReason} (see Settings ▸ Open logs)");
                        Log.Warn($"{node.Name}: {missing} track(s) failed — {partialReason}");
                    }
                    else
                    {
                        node.State = DownloadState.Done;
                        node.Progress = 1;
                        node.StatusText = tracks.Count > 1 ? $"Done · {tracks.Count} tracks" : "Done";
                    }
                }
                catch (OperationCanceledException)
                {
                    node.State = DownloadState.Failed;
                    node.StatusText = "Cancelled";
                    break;
                }
                catch (Exception ex)
                {
                    node.State = DownloadState.Failed;
                    node.StatusText = $"Failed — {Shorten(ex.Message)}";
                    SetDownloadStatus($"{node.Name}: {ex.Message} (see Settings ▸ Open logs)");
                    Log.Error($"Download failed for {node.Name} ({node.Url})", ex);
                }
                UpdateAggregateProgress(queue);
                SaveFailedDownloadQueue(); // durable after every item, not just once the whole run finishes (task 149)
            }
        }
        finally
        {
            _downloading = false;
            _downloadCts?.Dispose();
            _downloadCts = null;
            DownloadButton.Content = "⭳  Download";
            SpinIndicator(DownloadSpinner, false);

            if (imported > 0)
            {
                SaveLibrary();
                RenderLibrary();
            }

            var done = DownloadUnits().Count(n => n.State == DownloadState.Done);
            var failed = DownloadUnits().Count(n => n.State == DownloadState.Failed);
            SetDownloadStatus(failed == 0
                ? $"Finished — imported {imported} track{(imported == 1 ? "" : "s")}"
                : $"Finished — {done} done, {failed} failed, imported {imported} track{(imported == 1 ? "" : "s")}");

            if (done > 0) PruneSucceeded();
            SaveFailedDownloadQueue();
            RefreshDownloadChrome();
        }
    }

    /// <summary>
    /// Imports one already-finalized download (relocating it per the Copy/Move
    /// import setting) as soon as it lands, rather than waiting for the rest of
    /// the album (task 121). Adds it to the library immediately so it shows up
    /// while the rest keeps downloading.
    /// </summary>
    private async Task<int> ImportFinalizedFileAsync(string path)
    {
        try
        {
            var tracks = await Task.Run(() => MusicImporter.Import([path]));
            foreach (var track in tracks) _tracks.Add(track);
            if (tracks.Count > 0 && _source == LibrarySource.Music) RenderLibrary();
            return tracks.Count;
        }
        catch (Exception ex)
        {
            Log.Warn($"Progressive import failed for {path}: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// After a run, drop everything that downloaded cleanly and keep everything
    /// that failed (plus the album / artist rows above it) so a retry only has
    /// to cover what's left.
    /// </summary>
    private void PruneSucceeded()
    {
        foreach (var root in _rootNodes.ToList())
        {
            Prune(root);
            var emptyContainer = root.Kind is DownloadKind.Artist && root.Children.Count == 0;
            if (root.State == DownloadState.Done || emptyContainer)
                _rootNodes.Remove(root);
        }

        static void Prune(DownloadNode node)
        {
            foreach (var child in node.Children.ToList())
            {
                Prune(child);
                var succeeded = child.State == DownloadState.Done
                    || (child.Kind == DownloadKind.Track && node.State == DownloadState.Done);
                var emptyContainer = child.Kind is DownloadKind.Artist or DownloadKind.Album
                    && child.CanHaveChildren && child.Children.Count == 0 && child.State == DownloadState.Done;
                if (succeeded || emptyContainer)
                    node.Children.Remove(child);
            }
        }
    }

    // ---- Progress + status chrome --------------------------------------

    private void UpdateAggregateProgress(IReadOnlyList<DownloadNode> queue)
    {
        if (queue.Count == 0) { DownloadProgressBar.Value = 0; return; }
        var completed = queue.Count(n => n.State is DownloadState.Done or DownloadState.Importing);
        var partial = queue.Where(n => n.State == DownloadState.Downloading).Sum(n => n.Progress);
        DownloadProgressBar.Value = Math.Clamp((completed + partial) / queue.Count, 0, 1);
        DownloadProgressLabel.Text = $"{completed} of {queue.Count} downloaded";
    }

    private void RefreshDownloadChrome()
    {
        var units = DownloadUnits();
        var total = units.Count;
        var done = units.Count(n => n.State == DownloadState.Done);
        DownloadEmptyHint.Visibility = _rootNodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LinksTree.Visibility = _rootNodes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        DownloadProgressLabel.Text = $"{done} of {total} downloaded";
        if (!_downloading)
            DownloadProgressBar.Value = total == 0 ? 0 : (double)done / total;
        UpdateDownloadButtonState();
    }

    private void UpdateDownloadButtonState()
    {
        var hasWork = DownloadUnits().Any(n => n.State is not DownloadState.Done);
        DownloadButton.IsEnabled = _downloading || (hasWork && ToolManager.ToolsPresent);
    }

    /// <summary>Trims a failure reason to something that fits a status cell; the full text goes to the log.</summary>
    private static string Shorten(string text)
    {
        text = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return text.Length <= 60 ? text : text[..57].TrimEnd() + "…";
    }

    private void SetDownloadStatus(string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => SetDownloadStatus(message)); return; }
        DownloadStatusBar.Text = message;
    }

    private void BlurBehind(bool on)
    {
        DownloadDim.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        RootGrid.Effect = on ? new BlurEffect { Radius = 8 } : null;
    }
}
