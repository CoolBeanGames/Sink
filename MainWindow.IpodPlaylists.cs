using System.Windows;
using System.Windows.Input;
using Sink.Models;

namespace Sink;

/// <summary>
/// The iPod's Playlists subcategory (task 134): playlists currently synced
/// to the device — double-click to view its tracks, or Unsync to remove the
/// playlist grouping from the device (its tracks are left alone, matching
/// how deleting a library playlist already works). Self-contained, same
/// shape as MainWindow.IpodPodcasts.cs.
/// </summary>
public partial class MainWindow
{
    private bool _ipodPlaylistsActive;
    private string? _ipodPlaylistName;

    private sealed record IpodPlaylistRow(string Name, int Count);

    private void IpodPlaylistsButton_Click(object sender, RoutedEventArgs e)
    {
        _source = LibrarySource.Ipod;
        ApplySourceChrome();
        _ipodPlaylistsActive = true;
        _ipodPodcastsActive = false;
        _ipodPlaylistName = null;
        SetActiveNavigation(IpodPlaylistsButton);
        RenderIpodPlaylists();
    }

    /// <summary>Device track id -> adapted Track, paired by index with the raw read (the only place a device-native track id survives).</summary>
    private Dictionary<int, Track> IpodTracksByDeviceId()
    {
        var result = new Dictionary<int, Track>();
        if (_ipodLibrary is null) return result;
        var raw = _ipodLibrary.Tracks;
        for (var i = 0; i < raw.Count && i < _ipodTracks.Count; i++)
            result[raw[i].TrackId] = _ipodTracks[i];
        return result;
    }

    private void RenderIpodPlaylists()
    {
        GroupsScroller.Visibility = Visibility.Collapsed;
        TracksBorder.Visibility = Visibility.Collapsed;
        IpodPodcastsArea.Visibility = Visibility.Collapsed;
        IpodPlaylistsArea.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Collapsed;

        var playlists = (_ipodLibrary?.Playlists ?? []).Where(p => !p.IsMaster && !p.IsSmart && p.Name != "Podcasts").ToList();
        if (_ipodPlaylistName is null)
        {
            IpodPlaylistsList.Visibility = Visibility.Visible;
            IpodPlaylistTracksList.Visibility = Visibility.Collapsed;
            ViewTitle.Text = "iPod · Playlists";
            ViewSubtitle.Text = $"{playlists.Count} playlist{(playlists.Count == 1 ? "" : "s")} synced";
            IpodPlaylistsList.ItemsSource = playlists
                .Select(p => new IpodPlaylistRow(p.Name, p.TrackIds.Count))
                .OrderBy(r => r.Name)
                .ToList();
        }
        else
        {
            IpodPlaylistsList.Visibility = Visibility.Collapsed;
            IpodPlaylistTracksList.Visibility = Visibility.Visible;
            var playlist = playlists.FirstOrDefault(p => p.Name == _ipodPlaylistName);
            var byDeviceId = IpodTracksByDeviceId();
            var tracks = playlist?.TrackIds.Select(id => byDeviceId.GetValueOrDefault(id)).Where(t => t is not null).Select(t => t!).ToList() ?? [];
            ViewTitle.Text = _ipodPlaylistName;
            ViewSubtitle.Text = $"{tracks.Count} track{(tracks.Count == 1 ? "" : "s")}";
            IpodPlaylistTracksGrid.ItemsSource = tracks;
        }
    }

    private void IpodPlaylist_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not IpodPlaylistRow row) return;
        _ipodPlaylistName = row.Name;
        RenderIpodPlaylists();
    }

    private void IpodPlaylistBack_Click(object sender, RoutedEventArgs e)
    {
        _ipodPlaylistName = null;
        RenderIpodPlaylists();
    }

    private async void IpodPlaylistUnsync_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not IpodPlaylistRow row) return;
        var root = _ipodDevice?.LibraryRoot;
        if (root is null) return;
        if (_ipodWriting) { PlaybackStatus.Text = "iPod is busy…"; return; }

        _ipodWriting = true;
        StartIpodSync(indefinite: true);
        try
        {
            var removed = await Task.Run(() => Services.Ipod.IpodWriteService.RemovePlaylist(root, row.Name));
            PlaybackStatus.Text = removed ? $"Unsynced playlist \"{row.Name}\"" : $"Couldn't unsync \"{row.Name}\"";
        }
        catch (Exception ex)
        {
            Services.Log.Error("IpodPlaylistUnsync threw", ex);
            PlaybackStatus.Text = $"iPod update failed: {ex.Message}";
        }
        finally
        {
            _ipodWriting = false;
            StopIpodSync();
            LoadIpodLibrary(root);
        }
    }
}
