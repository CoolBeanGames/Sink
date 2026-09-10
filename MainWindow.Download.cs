using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Effects;
using Sink.Dialogs;
using Sink.Services;
using Sink.Services.Download;

namespace Sink;

/// <summary>
/// The Download page: paste YouTube links, scan them for metadata, tweak the
/// details, then hand them to yt-dlp one by one and import the results.
/// </summary>
public partial class MainWindow
{
    private readonly ObservableCollection<DownloadItem> _downloadItems = [];
    private bool _downloadViewActive;
    private bool _toolsChecked;
    private bool _downloading;
    private CancellationTokenSource? _downloadCts;

    private void InitDownloadPage()
    {
        LinksGrid.ItemsSource = _downloadItems;
        _downloadItems.CollectionChanged += (_, _) => RefreshDownloadChrome();
        RefreshDownloadChrome();
    }

    // ---- View switching -------------------------------------------------

    private void ShowDownloadSource_Click(object sender, RoutedEventArgs e)
    {
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

    // ---- Link list -------------------------------------------------------

    private async void AddLink_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddLinkWindow { Owner = this };
        BlurBehind(true);
        var ok = dialog.ShowDialog() == true;
        BlurBehind(false);
        if (!ok) return;

        var item = new DownloadItem { Url = dialog.Link, State = DownloadState.Pending, StatusText = "Scanning…" };
        _downloadItems.Add(item);
        await ScanItemAsync(item);
    }

    private async Task ScanItemAsync(DownloadItem item)
    {
        item.State = DownloadState.Scanning;
        item.StatusText = "Scanning…";
        try
        {
            var info = await Task.Run(() => DownloadService.ScanAsync(item.Url));
            if (string.IsNullOrWhiteSpace(item.Title)) item.Title = info.Title;
            if (string.IsNullOrWhiteSpace(item.Artist)) item.Artist = info.Artist;
            if (string.IsNullOrWhiteSpace(item.Album)) item.Album = info.Album;
            if (string.IsNullOrWhiteSpace(item.Genre)) item.Genre = info.Genre;
            item.IsPlaylist = info.IsPlaylist;
            item.TrackCount = info.TrackCount;
            item.State = DownloadState.Ready;
            item.StatusText = info.IsPlaylist ? $"Album · {info.TrackCount} tracks" : "Ready";
        }
        catch (Exception ex)
        {
            item.State = DownloadState.Failed;
            item.StatusText = "Couldn't scan";
            SetDownloadStatus($"Scan failed for {item.Url}: {ex.Message}");
        }
        UpdateDownloadButtonState();
    }

    private void DeleteLink_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadItem item)
            _downloadItems.Remove(item);
    }

    private void LinksGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        // The edited value is already pushed to the item by the binding; just
        // make sure a hand-edited row is no longer treated as failed.
        if (e.Row.Item is DownloadItem { State: DownloadState.Failed } item && !string.IsNullOrWhiteSpace(item.Url))
        {
            item.State = DownloadState.Ready;
            item.StatusText = "Ready";
            UpdateDownloadButtonState();
        }
    }

    // ---- Options --------------------------------------------------------

    private DownloadOptions ReadOptions() => new()
    {
        WriteMetadata = OptMetadata.IsChecked == true,
        EmbedAlbumArt = OptAlbumArt.IsChecked == true,
        PreferMusicMetadata = OptMusicMeta.IsChecked == true,
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

    // ---- Download run ---------------------------------------------------

    private async void StartDownloads_Click(object sender, RoutedEventArgs e)
    {
        if (_downloading)
        {
            _downloadCts?.Cancel();
            return;
        }

        var queue = _downloadItems.Where(i => i.State is not DownloadState.Done).ToList();
        if (queue.Count == 0) { SetDownloadStatus("Add a link first"); return; }
        if (!ToolManager.ToolsPresent) { SetDownloadStatus("Still setting up yt-dlp…"); EnsureToolsReady(); return; }

        var options = ReadOptions();
        _downloading = true;
        _downloadCts = new CancellationTokenSource();
        var token = _downloadCts.Token;
        DownloadButton.Content = "Stop";
        var imported = 0;

        try
        {
            for (var index = 0; index < queue.Count && !token.IsCancellationRequested; index++)
            {
                var item = queue[index];
                item.State = DownloadState.Downloading;
                item.Progress = 0;
                item.StatusText = "Starting…";
                var linkLabel = queue.Count > 1 ? $"  (link {index + 1}/{queue.Count})" : "";

                try
                {
                    var progress = new Progress<double>(p =>
                    {
                        item.Progress = p;
                        item.StatusText = $"Downloading {p * 100:0}%";
                        UpdateAggregateProgress(queue);
                    });
                    var status = new Progress<string>(s => SetDownloadStatus(s + linkLabel));
                    var paths = await DownloadService.DownloadAsync(item, options, progress, status, token);

                    item.State = DownloadState.Importing;
                    item.StatusText = "Importing…";
                    var tracks = await Task.Run(() => MusicImporter.Import(paths));
                    foreach (var track in tracks) _tracks.Add(track);
                    imported += tracks.Count;

                    item.State = DownloadState.Done;
                    item.Progress = 1;
                    item.StatusText = tracks.Count > 1 ? $"Done · {tracks.Count} tracks" : "Done";
                }
                catch (OperationCanceledException)
                {
                    item.State = DownloadState.Failed;
                    item.StatusText = "Cancelled";
                    break;
                }
                catch (Exception ex)
                {
                    item.State = DownloadState.Failed;
                    item.StatusText = "Failed";
                    SetDownloadStatus($"{item.Title}: {ex.Message}");
                }
                UpdateAggregateProgress(queue);
            }
        }
        finally
        {
            _downloading = false;
            _downloadCts?.Dispose();
            _downloadCts = null;
            DownloadButton.Content = "⭳  Download";

            if (imported > 0)
            {
                SaveLibrary();
                RenderLibrary();
            }

            var done = _downloadItems.Count(i => i.State == DownloadState.Done);
            var failed = _downloadItems.Count(i => i.State == DownloadState.Failed);
            SetDownloadStatus(failed == 0
                ? $"Finished — imported {imported} track{(imported == 1 ? "" : "s")}"
                : $"Finished — {done} done, {failed} failed, imported {imported} track{(imported == 1 ? "" : "s")}");

            // Clear the list once everything that could finish has finished.
            if (done > 0 && failed == 0)
            {
                foreach (var item in _downloadItems.Where(i => i.State == DownloadState.Done).ToList())
                    _downloadItems.Remove(item);
            }
            RefreshDownloadChrome();
        }
    }

    // ---- Progress + status chrome --------------------------------------

    private void UpdateAggregateProgress(IReadOnlyList<DownloadItem> queue)
    {
        if (queue.Count == 0) { DownloadProgressBar.Value = 0; return; }
        var completed = queue.Count(i => i.State is DownloadState.Done or DownloadState.Importing);
        var partial = queue.Where(i => i.State == DownloadState.Downloading).Sum(i => i.Progress);
        DownloadProgressBar.Value = Math.Clamp((completed + partial) / queue.Count, 0, 1);
        DownloadProgressLabel.Text = $"{completed} of {queue.Count} downloaded";
    }

    private void RefreshDownloadChrome()
    {
        var total = _downloadItems.Count;
        var done = _downloadItems.Count(i => i.State == DownloadState.Done);
        DownloadEmptyHint.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
        LinksGrid.Visibility = total == 0 ? Visibility.Collapsed : Visibility.Visible;
        DownloadProgressLabel.Text = $"{done} of {total} downloaded";
        if (!_downloading)
            DownloadProgressBar.Value = total == 0 ? 0 : (double)done / total;
        UpdateDownloadButtonState();
    }

    private void UpdateDownloadButtonState()
    {
        var hasWork = _downloadItems.Any(i => i.State is not DownloadState.Done);
        DownloadButton.IsEnabled = _downloading || (hasWork && ToolManager.ToolsPresent);
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
