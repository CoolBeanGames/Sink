using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using Sink.Dialogs;
using Sink.Services;
using Sink.Services.LanSync;

namespace Sink;

public partial class MainWindow
{
    private bool _pcSyncRecoveryPending;

    private void SyncComputers(Window owner)
    {
        try
        {
            EnsurePcSyncReady();
            var window = new LanSyncWindow(CapturePcSyncSnapshot, ApplyPcSync) { Owner = owner };
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            Log.Error("PC sync could not start", ex);
            MessageBox.Show(owner, ex.Message, "Sync with another PC", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void EnsurePcSyncReady()
    {
        if (_ipodWriting || _syncSessionActive || _downloading || _downloadImportGate.CurrentCount == 0
            || _podcastDownloads > 0 || _podcastAutomationGate.CurrentCount == 0 || _podcastRefreshGate.CurrentCount == 0)
            throw new InvalidOperationException("Wait for downloads, podcast refreshes, or iPod sync to finish, then try again.");
        if (_isPlaying || _playingEpisode is not null)
            throw new InvalidOperationException("Pause music and finish the current podcast before syncing with another PC.");
    }

    private LanLocalData CurrentPcData() => new(new LibraryData
    {
        Tracks = _tracks.ToList(),
        Playlists = _playlists.Select(p => new PlaylistData { Id = p.Id, Name = p.Name, TrackIds = p.TrackIds.ToList() }).ToList(),
        SyncedTrackIds = _syncedTrackIds.ToList(),
    }, _podcasts.ToList(), _listenEvents.ToList());

    private LanSnapshot CapturePcSyncSnapshot()
    {
        EnsurePcSyncReady();
        BackfillListenSnapshots();
        var settings = AppSettings.Current;
        return LanProjection.Capture(CurrentPcData(), settings.LibraryLocation, PodcastStore.Folder, settings.HighQualityRoot);
    }

    private void ApplyPcSync(LanSyncStore store, LanTrust peer, LanSnapshot snapshot, JsonObject baseline)
    {
        EnsurePcSyncReady();
        var settings = AppSettings.Current;
        var old = CurrentPcData();
        var data = LanProjection.Restore(snapshot, old, settings.LibraryLocation, PodcastStore.Folder, settings.HighQualityRoot);
        var playing = _nowPlaying is not null && old.Library.Tracks.Any(t => t.Id == _nowPlaying.Id) ? _nowPlaying : null;
        var activePlaylist = _activePlaylist?.Id;
        var currentShow = _currentShow?.Id;
        try { store.Commit(peer, data, baseline, PodcastStore.Folder, old.Podcasts); }
        catch (LanRecoveryRequiredException ex)
        {
            _pcSyncRecoveryPending = true;
            Log.Error("PC sync requires startup recovery", ex);
            MessageBox.Show(ex.Message, "PC sync recovery", MessageBoxButton.OK, MessageBoxImage.Error);
            Application.Current.Shutdown(1);
            throw;
        }

        ReplaceLibraryContents(data.Library);
        _podcasts.Clear();
        _podcasts.AddRange(data.Podcasts);
        _listenEvents.Clear();
        _listenEvents.AddRange(data.Listens);
        // Update the copy that travels with a mounted podcast folder as well.
        PodcastStore.Save(_podcasts);
        _currentShow = _podcasts.FirstOrDefault(show => show.Id == currentShow);
        if (_currentShow is null && _podcastMode == PodcastMode.Show) _podcastMode = PodcastMode.Library;
        if (playing is not null)
        {
            _nowPlaying = _tracks.FirstOrDefault(t => t.Id == playing.Id)
                ?? (playing.FilePath is not null ? _tracks.FirstOrDefault(t => string.Equals(t.FilePath, playing.FilePath, StringComparison.OrdinalIgnoreCase)) : null);
            if (_nowPlaying is null)
            {
                _mediaPlayer.Close();
                _playbackTimer.Stop();
                PlayerTitle.Text = "Choose something to play";
                PlayerArtist.Text = "Your library is ready";
                PlayerArtInitial.Text = "♫";
                PlayPauseButton.Content = "▶";
                SetNowPlayingArt(null);
            }
        }
        _activePlaylist = _playlists.FirstOrDefault(p => p.Id == activePlaylist);
        PlaylistList.Items.Refresh();
        _artCache.Clear();
        _greyArtCache.Clear();
        RenderLibrary();
        RenderPodcasts();
        UpdatePodcastSidebarDot();
        if (_reflectViewActive) RenderReflect();
        _tagsView?.Refresh();
        ScheduleLibrarySizeUpdate();
        _ = ScanForMissingFilesAsync();
        PlaybackStatus.Text = $"Synced databases with {peer.Name}";
    }
}
