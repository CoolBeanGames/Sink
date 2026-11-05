using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Sink.Models;

namespace Sink.Controls;

/// <summary>A live shuffle toggle. Changing the local preference never waits for device I/O.</summary>
internal static class ShuffleMenu
{
    public static MenuItem Create(IReadOnlyList<Track> selection, Func<bool, Task> preferenceChanged)
    {
        var tracks = selection.Distinct().ToArray();
        var item = new MenuItem { IsEnabled = tracks.Length > 0 };
        void RefreshHeader() => item.Header = tracks.Length > 0 && tracks.All(t => t.ExcludedFromShuffle)
            ? "Include in shuffle" : "Exclude from shuffle";

        void TrackChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (item.Dispatcher.CheckAccess()) RefreshHeader();
            else item.Dispatcher.BeginInvoke((Action)RefreshHeader);
        }
        foreach (var track in tracks)
            PropertyChangedEventManager.AddHandler(track, TrackChanged, nameof(Track.ExcludedFromShuffle));

        item.Click += async (_, e) =>
        {
            e.Handled = true;
            // Read the current state: the same menu item can be reused, and a
            // pending device write must not freeze or reverse the next click.
            var exclude = !tracks.All(t => t.ExcludedFromShuffle);
            foreach (var track in tracks) track.ExcludedFromShuffle = exclude;
            RefreshHeader();
            await preferenceChanged(exclude);
        };
        RefreshHeader();
        return item;
    }
}
