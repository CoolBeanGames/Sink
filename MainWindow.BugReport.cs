using System.Windows;
using Sink.Services;

namespace Sink;

/// <summary>In-app "Report a bug" — files straight into this project's own Zen queue via zen-operator, tagged for whatever page is currently showing.</summary>
public partial class MainWindow
{
    private void ReportBug_Click(object sender, RoutedEventArgs e)
    {
        var tags = DetectPageTags();
        var dialog = new BugReportWindow(tags) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var (ok, message) = ZenOperator.FileBug(dialog.BugTitle, dialog.Description, tags);
        PlaybackStatus.Text = ok ? "Bug filed" : $"Couldn't file bug: {message}";
        if (!ok) Log.Error($"ReportBug: {message}");
    }

    /// <summary>
    /// Reads whichever of the five top-level page Grids is currently
    /// visible (and, inside MusicPage, whether it's the iPod mirror or the
    /// local library, and what sub-view within it) to pick tags matching
    /// the project's own Zen tag catalog.
    /// </summary>
    private List<string> DetectPageTags()
    {
        if (DownloadPage.Visibility == Visibility.Visible) return ["download"];
        if (TagsPage.Visibility == Visibility.Visible) return ["tags"];
        if (ReflectPage.Visibility == Visibility.Visible) return ["reflect"];
        if (PodcastPage.Visibility == Visibility.Visible) return ["Podcasts"];

        if (_source == LibrarySource.Ipod)
        {
            var tags = new List<string> { "ipod" };
            if (_ipodPodcastsActive) tags.Add("Podcasts");
            if (_ipodPlaylistsActive) tags.Add("playlists");
            return tags;
        }

        var musicTags = new List<string> { "music", "library" };
        if (_activePlaylist is not null) musicTags.Add("playlists");
        return musicTags;
    }
}
