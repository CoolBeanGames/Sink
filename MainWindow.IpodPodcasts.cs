using System.Windows;
using System.Windows.Input;
using Sink.Models;

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
    private string? _ipodPodcastShow;

    private sealed record IpodPodcastShowRow(string Title, string Artist, int Count);

    private void IpodPodcastsButton_Click(object sender, RoutedEventArgs e)
    {
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

    private void RenderIpodPodcasts()
    {
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
            var showEpisodes = episodes.Where(t => t.Album == _ipodPodcastShow).OrderBy(t => t.Title).ToList();
            ViewTitle.Text = _ipodPodcastShow;
            ViewSubtitle.Text = $"{showEpisodes.Count} episode{(showEpisodes.Count == 1 ? "" : "s")}";
            IpodPodcastEpisodesGrid.ItemsSource = showEpisodes;
        }
    }

    private void IpodPodcastShow_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not IpodPodcastShowRow row) return;
        _ipodPodcastShow = row.Title;
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
        UnsyncTracks([track]);
    }
}
