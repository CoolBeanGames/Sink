using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Sink.Dialogs;
using Sink.Services;
using Sink.Services.Download;

namespace Sink;

public partial class MainWindow
{
    private readonly Dictionary<(MusicSearchResult Result, string Source), DownloadNode> _searchBrowseNodes = new();

    private static string PreferredSearchSource(MusicSearchResult result) =>
        result.HasDeezer ? "Deezer" : result.HasSpotify ? "Spotify" : "YouTube";

    private async Task<DownloadNode> LoadSearchNodeAsync(MusicSearchResult result, string source, CancellationToken token)
    {
        if (!_searchBrowseNodes.TryGetValue((result, source), out var node))
        {
            var url = source switch
            {
                "Deezer" => result.DeezerUrl,
                "YouTube" => result.YouTubeUrl,
                _ => MusicSearchService.SpotifyMatchUrl(result),
            };
            var info = await Task.Run(() => DownloadService.ScanAsync(url, token), token);
            token.ThrowIfCancellationRequested();
            node = BuildNode(url, info);
            if (result.Kind == MusicSearchResultKind.Artist && node.Kind != DownloadKind.Artist
                || result.Kind == MusicSearchResultKind.Album && node.Kind != DownloadKind.Album)
                throw new InvalidOperationException("This source did not return a browsable album list or track list. Try another search result.");
        }
        var artwork = await MusicSearchService.CacheArtworkAsync(result.Artwork, token);
        ApplySearchMetadata(node, result, artwork);
        _searchBrowseNodes[(result, source)] = node;
        return node;
    }

    private async Task LoadBrowseAlbumAsync(DownloadNode album, CancellationToken token)
    {
        if (album.Scanned) return;
        var enabled = album.Enabled;
        var info = await Task.Run(() => DownloadService.ScanAsync(album.Url, token), token);
        token.ThrowIfCancellationRequested();
        if (!info.IsPlaylist || info.TrackTitles is not { Count: > 0 })
            throw new InvalidOperationException("This source returned no tracks for the album.");
        FillTracks(album, info);
        album.Scanned = true;
        album.Enabled = enabled;
        RefreshDownloadNodeStatus(album);
    }

    private void MusicSearchResults_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var container = ItemsControl.ContainerFromElement(MusicSearchResults, e.OriginalSource as DependencyObject) as ListBoxItem;
        MusicSearchResults.SelectedItem = container?.DataContext;
        if (container is not null) container.Focus();
    }

    private async Task BrowseSearchResultAsync(MusicSearchResult result)
    {
        var source = PreferredSearchSource(result);
        var dialog = new DownloadBrowseWindow(result.Title,
            result.Kind == MusicSearchResultKind.Artist,
            token => LoadSearchNodeAsync(result, source, token), LoadBrowseAlbumAsync) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.RequestedDownload is not { } selection) return;

        var queued = DownloadSelection.Snapshot(selection);
        var units = DownloadSelection.PendingUnits(queued).ToList();
        if (units.Count == 0) return;
        _rootNodes.Add(queued);
        SaveFailedDownloadQueue();
        RefreshDownloadChrome();
        if (_downloading)
        {
            SetDownloadStatus($"Queued “{queued.Name}” — start the next download set when the current set finishes");
            return;
        }
        try { await RunDownloadQueueAsync(units); }
        catch (Exception ex)
        {
            Log.Error("Download from search browser failed", ex);
            SetDownloadStatus($"Couldn't finish the selected downloads: {Shorten(ex.Message)}");
        }
    }
}
