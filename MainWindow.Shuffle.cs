using System.IO;
using System.Windows.Threading;
using Sink.Models;
using Sink.Services.Ipod;

namespace Sink;

public partial class MainWindow
{
    private readonly PendingShufflePreferences _pendingShuffle = new();
    private DispatcherTimer? _shuffleUpdateTimer;
    private bool _flushingShuffle;
    private bool _shufflePreferencesDirty;

    private void SaveShufflePreferences()
    {
        _shufflePreferencesDirty = true;
        if (!_ipodWriting && !_syncSessionActive && !_flushingShuffle)
        {
            SaveLibrary();
            _shufflePreferencesDirty = false;
        }
        else ResumePendingShuffleUpdates();
    }

    private void QueueShuffleUpdate(string root, IReadOnlyList<Track> tracks, bool excluded, bool matchDevicePaths)
    {
        _pendingShuffle.Enqueue(root, tracks, excluded, matchDevicePaths);
        ResumePendingShuffleUpdates();
    }

    private void ResumePendingShuffleUpdates()
    {
        if (_pendingShuffle.Count == 0 && !_shufflePreferencesDirty) return;
        if (_shuffleUpdateTimer is null)
        {
            _shuffleUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _shuffleUpdateTimer.Tick += async (_, _) => await FlushShuffleUpdatesAsync();
        }
        _shuffleUpdateTimer.Start();
    }

    private async Task FlushShuffleUpdatesAsync()
    {
        if (_flushingShuffle || _ipodWriting || _syncSessionActive) return;
        if (_shufflePreferencesDirty) { SaveLibrary(); _shufflePreferencesDirty = false; }
        if (_pendingShuffle.Count == 0) { _shuffleUpdateTimer?.Stop(); return; }
        var root = _ipodDevice?.LibraryRoot;
        if (root is null) { _shuffleUpdateTimer?.Stop(); return; }
        var batch = _pendingShuffle.Values.Where(p => string.Equals(p.Root, root, StringComparison.OrdinalIgnoreCase)).OrderBy(p => p.Revision).ToList();
        if (batch.Count == 0) { _shuffleUpdateTimer?.Stop(); return; }
        _flushingShuffle = true; _ipodWriting = true;
        StartIpodSync(indefinite: true);
        try
        {
            for (var index = 0; index < batch.Count;)
            {
                // Batch adjacent equal operations only, so overlapping local
                // and device selections retain their chronological order.
                var first = batch[index];
                var pending = new List<PendingShufflePreferences.Update>();
                while (index < batch.Count && batch[index].Excluded == first.Excluded && batch[index].MatchDevicePaths == first.MatchDevicePaths)
                    pending.Add(batch[index++]);
                var snapshots = pending.Select(p => p.Track).ToList();
                var matched = await Task.Run(() => IpodMaintenance.SetShuffleExclusion(root, snapshots, first.Excluded, first.MatchDevicePaths));
                foreach (var update in pending) _pendingShuffle.Acknowledge(update);
                PlaybackStatus.Text = matched > 0 ? $"Shuffle setting verified on iPod ({matched} tracks)"
                    : "Shuffle preference saved — these tracks still need to be synced to the iPod";
            }
        }
        catch (Exception ex)
        {
            Services.Log.Error("Could not save queued iPod shuffle settings", ex);
            PlaybackStatus.Text = "Shuffle preference saved in Sink; iPod update failed. Reconnect or sync to retry.";
            _shuffleUpdateTimer?.Stop();
        }
        finally
        {
            _ipodWriting = false; _flushingShuffle = false; StopIpodSync();
            if (_pendingShuffle.Count == 0) _shuffleUpdateTimer?.Stop();
        }
    }

    /// <summary>A device read finishing during a toggle must not overwrite the latest UI intent.</summary>
    private void ApplyPendingShufflePreferences(string root)
    {
        if (_ipodLibrary is null) return;
        foreach (var pending in _pendingShuffle.Values.Where(p => string.Equals(p.Root, root, StringComparison.OrdinalIgnoreCase)))
        {
            var track = pending.Track;
            var key = IpodDbTrack.MakeKey(track.Title, track.Artist, track.Album, track.TrackNumber);
            foreach (var row in _ipodLibrary.Tracks)
            {
                var matches = pending.MatchDevicePaths
                    ? !string.IsNullOrWhiteSpace(track.FilePath) && string.Equals(Path.GetFullPath(row.FilePath), Path.GetFullPath(track.FilePath), StringComparison.OrdinalIgnoreCase)
                    : row.Key == key || row.Key == track.LastSyncedKey;
                if (matches) row.ExcludedFromShuffle = pending.Excluded;
            }
        }
    }
}
