using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Sink.Models;
using Sink.Services;

namespace Sink;

/// <summary>
/// The iPod's Podcasts subcategory (task 133): episodes currently synced to
/// the device, grouped by show — double-click a show to see its episodes,
/// each with an Unsync (delete-from-device) button. Lives inside the same
/// MusicPage area as Albums/Artists/Genres/Songs but is otherwise a
/// self-contained mode, independent of the LibraryCategory/RenderLibrary
/// pipeline those use (which assumes plain music track grouping).
/// </summary>
public partial class MainWindow
{
    private bool _ipodPodcastsActive;
    private (string Title, string Artist)? _ipodPodcastShow;

    private sealed record IpodPodcastShowRow(string Title, string Artist, int Count);

    private void IpodPodcastsButton_Click(object sender, RoutedEventArgs e)
    {
        _libraryHubActive = false;
        _source = LibrarySource.Ipod;
        ApplySourceChrome();
        _ipodPodcastsActive = true;
        _ipodPlaylistsActive = false;
        _ipodPodcastShow = null;
        SetActiveNavigation(IpodPodcastsButton);
        RenderIpodPodcasts();
    }

    /// <summary>The adapted iPod tracks that are podcast episodes — paired by index with the raw device read, which is the only place "is this a podcast" (PodcastFlag/MediaType) survives.</summary>
    private List<Track> IpodPodcastEpisodes()
    {
        if (_ipodLibrary is null) return [];
        var result = new List<Track>();
        var raw = _ipodLibrary.Tracks;
        for (var i = 0; i < raw.Count && i < _ipodTracks.Count; i++)
            if (raw[i].IsPodcast) result.Add(_ipodTracks[i]);
        return result;
    }

    /// <summary>The adapted iPod tracks that are music (everything but podcast episodes), paired by index with the raw read like <see cref="IpodPodcastEpisodes"/>.</summary>
    private List<Track> IpodMusicTracks()
    {
        if (_ipodLibrary is null) return [];
        var result = new List<Track>();
        var raw = _ipodLibrary.Tracks;
        for (var i = 0; i < raw.Count && i < _ipodTracks.Count; i++)
            if (!raw[i].IsPodcast) result.Add(_ipodTracks[i]);
        return result;
    }
    private void RenderIpodPodcasts()
    {
        LibraryHub.Visibility = Visibility.Collapsed;
        LibrarySearchBorder.Visibility = Visibility.Collapsed;
        GroupsScroller.Visibility = Visibility.Collapsed;
        TracksBorder.Visibility = Visibility.Collapsed;
        IpodPlaylistsArea.Visibility = Visibility.Collapsed;
        IpodPodcastsArea.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Collapsed;

        var episodes = IpodPodcastEpisodes();
        if (_ipodPodcastShow is null)
        {
            IpodPodcastShowsList.Visibility = Visibility.Visible;
            IpodPodcastEpisodesList.Visibility = Visibility.Collapsed;
            ViewTitle.Text = "iPod · Podcasts";
            ViewSubtitle.Text = $"{episodes.Count} episode{(episodes.Count == 1 ? "" : "s")} synced";
            IpodPodcastShowsList.ItemsSource = episodes
                .GroupBy(t => (t.Album, t.Artist))
                .Select(g => new IpodPodcastShowRow(g.Key.Album, g.Key.Artist, g.Count()))
                .OrderBy(r => r.Title)
                .ToList();
        }
        else
        {
            IpodPodcastShowsList.Visibility = Visibility.Collapsed;
            IpodPodcastEpisodesList.Visibility = Visibility.Visible;
            var show = _ipodPodcastShow.Value;
            var showEpisodes = episodes.Where(t => (t.Album, t.Artist) == show).OrderBy(t => t.Title).ToList();
            if (showEpisodes.Count == 0)
            {
                _ipodPodcastShow = null;
                RenderIpodPodcasts();
                return;
            }
            ViewTitle.Text = show.Title;
            ViewSubtitle.Text = $"{showEpisodes.Count} episode{(showEpisodes.Count == 1 ? "" : "s")}";
            IpodPodcastEpisodesGrid.ItemsSource = showEpisodes;
        }
    }

    private void IpodPodcastShow_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(IpodPodcastShowsList, e.OriginalSource as DependencyObject) is ListBoxItem
            { DataContext: IpodPodcastShowRow show }) OpenIpodPodcastShow(show);
    }

    private void IpodPodcastShow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is IpodPodcastShowRow show) OpenIpodPodcastShow(show);
    }

    private void OpenIpodPodcastShow(IpodPodcastShowRow show)
    {
        _ipodPodcastShow = (show.Title, show.Artist);
        RenderIpodPodcasts();
    }

    private void IpodPodcastBack_Click(object sender, RoutedEventArgs e)
    {
        _ipodPodcastShow = null;
        RenderIpodPodcasts();
    }

    private void IpodPodcastUnsync_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Track track) return;
        RemoveIpodPodcastEpisodes([track]);
    }

    private void IpodPodcastUnsyncSelected_Click(object sender, RoutedEventArgs e) =>
        RemoveIpodPodcastEpisodes(IpodPodcastEpisodesGrid.SelectedItems.OfType<Track>().ToList());

    private void RemoveIpodPodcastEpisodes(IReadOnlyList<Track> tracks)
    {
        if (tracks.Count == 0) { PlaybackStatus.Text = "Select episodes to remove from the iPod"; return; }
        if (_ipodLibrary is null || _ipodDevice?.LibraryRoot is null) return;
        var paths = tracks.Select(t => t.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keys = _ipodLibrary.Tracks.Where(t => t.IsPodcast && paths.Contains(t.FilePath)).Select(t => t.Key).ToHashSet();
        UnsyncTracks(tracks, podcastsOnly: true, onRemoved: () =>
        {
            foreach (var show in _podcasts)
                foreach (var episode in show.Episodes)
                    if (keys.Contains(PodcastEpisodeKey(show, episode))) episode.ExcludeFromIpod = true;
            PodcastStore.Save(_podcasts);
        });
    }
}
