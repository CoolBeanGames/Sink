using System.IO;
using System.Windows;
using System.Windows.Controls;
using Sink.Controls;

namespace Sink;

/// <summary>
/// Experimental Cover Flow (coverflow branch): a full-window album shelf that
/// replaces the sidebar and pages while it's open. The player bar stays
/// visible underneath so playback started from the shelf can be controlled.
/// </summary>
public partial class MainWindow
{
    private CoverFlowView? _coverFlow;
    private GridLength _sidebarWidthBeforeCoverFlow;
    private double _sidebarMinWidthBeforeCoverFlow;

    private void CoverFlow_Click(object sender, RoutedEventArgs e) => OpenCoverFlow();

    private void OpenCoverFlow()
    {
        if (_coverFlow is not null) return;
        var albums = _tracks
            .GroupBy(t => t.Album)
            .Select(g =>
            {
                var tracks = g.OrderBy(t => t.TrackNumber == 0 ? int.MaxValue : t.TrackNumber).ThenBy(t => t.Title).ToList();
                var artist = tracks.GroupBy(t => t.Artist).OrderByDescending(a => a.Count()).First().Key;
                var art = tracks.Select(t => t.ArtworkPath).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
                return new CoverFlowAlbum(g.Key, artist, tracks[0].Genre, art, tracks);
            })
            .OrderBy(a => a.Artist, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Album, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _coverFlow = new CoverFlowView(albums);
        _coverFlow.CloseRequested += CloseCoverFlow;
        _coverFlow.PlayRequested += (tracks, index) =>
        {
            if (index >= 0 && index < tracks.Count) PlayTrack(tracks[index]);
        };

        // Hide the sidebar for the duration; restored exactly on close.
        _sidebarWidthBeforeCoverFlow = SidebarColumn.Width;
        _sidebarMinWidthBeforeCoverFlow = SidebarColumn.MinWidth;
        SidebarColumn.MinWidth = 0;
        SidebarColumn.Width = new GridLength(0);

        Grid.SetRow(_coverFlow, 0);
        Grid.SetColumn(_coverFlow, 0);
        Grid.SetColumnSpan(_coverFlow, 2);
        Panel.SetZIndex(_coverFlow, 60);
        RootGrid.Children.Add(_coverFlow);
        _coverFlow.Focus();
    }

    private void CloseCoverFlow()
    {
        if (_coverFlow is null) return;
        RootGrid.Children.Remove(_coverFlow);
        _coverFlow = null;
        SidebarColumn.Width = _sidebarWidthBeforeCoverFlow;
        SidebarColumn.MinWidth = _sidebarMinWidthBeforeCoverFlow;
    }
}
