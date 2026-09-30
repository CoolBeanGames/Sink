using System.IO;
using System.Windows;
using Microsoft.Win32;
using Sink.Dialogs;
using Sink.Models;
using Sink.Services;

namespace Sink;

/// <summary>Settings dialog wiring plus the library-maintenance actions it exposes.</summary>
public partial class MainWindow
{
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(
            AppSettings.Current.Clone(), RefreshLibraryAsync, ExportLibrary, ImportLibrary, OrganizeLibraryAsync,
            () => DownloadHighQuality(_tracks.ToList()))
            { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;
        dialog.Result.Save();
        PlaybackStatus.Text = "Settings saved";
    }

    /// <summary>
    /// Moves every file under the library folder into Artist/Album subfolders
    /// and deletes whatever's left empty afterward (task 155). Runs against
    /// whatever is currently saved as the library location, not an unsaved
    /// edit still sitting in the settings dialog.
    /// </summary>
    private async Task<string> OrganizeLibraryAsync(IProgress<LibraryMaintenanceProgress> progress)
    {
        // MusicImporter updates FilePath/FileName as it moves files. Give the
        // worker detached copies so no WPF-bound Track is mutated off-thread,
        // then apply only those path changes after await returns to the UI.
        var snapshots = _tracks.Select(MaintenanceCopy).ToList();
        var location = AppSettings.Current.LibraryLocation;
        var result = await Task.Run(() => MusicImporter.Organize(location, snapshots, progress));

        progress.Report(new LibraryMaintenanceProgress("Applying organized paths…"));
        var byId = _tracks.ToDictionary(track => track.Id);
        foreach (var updated in snapshots)
        {
            if (!byId.TryGetValue(updated.Id, out var track)) continue;
            track.FilePath = updated.FilePath;
            track.FileName = updated.FileName;
        }
        if (result.Moved > 0)
        {
            progress.Report(new LibraryMaintenanceProgress("Saving the library…"));
            await SaveMaintenanceSnapshotAsync();
        }
        PlaybackStatus.Text = result.Moved > 0
            ? $"Organized library — moved {result.Moved} file{(result.Moved == 1 ? "" : "s")}"
            : "Library already organized";
        progress.Report(new LibraryMaintenanceProgress("Library organization complete", 1, 1));
        return result.Moved > 0 || result.FoldersRemoved > 0
            ? $"Moved {result.Moved} file{(result.Moved == 1 ? "" : "s")} into Artist/Album folders and removed {result.FoldersRemoved} empty folder{(result.FoldersRemoved == 1 ? "" : "s")}."
            : "Everything was already organized — nothing to move.";
    }

    /// <summary>Re-reads tags/art for every track and drops any whose file is gone. Returns the number kept.</summary>
    private async Task<int> RefreshLibraryAsync(IProgress<LibraryMaintenanceProgress> progress)
    {
        // TagLib and artwork extraction are the expensive part. Work on
        // detached snapshots, then marshal the small property/collection
        // update phase back to this captured WPF synchronization context.
        var snapshots = _tracks.Select(MaintenanceCopy).ToList();
        var scan = await Task.Run(() =>
        {
            var refreshed = new List<Track>(snapshots.Count);
            var missingIds = new List<Guid>();
            var reportEvery = Math.Max(1, snapshots.Count / 100);
            for (var index = 0; index < snapshots.Count; index++)
            {
                var track = snapshots[index];
                if (index % reportEvery == 0)
                    progress.Report(new LibraryMaintenanceProgress(
                        $"Reading tags for {track.FileName}", index, snapshots.Count));
                if (MusicImporter.RefreshTags(track)) refreshed.Add(track);
                else missingIds.Add(track.Id);
            }
            progress.Report(new LibraryMaintenanceProgress(
                "Tag scan complete", snapshots.Count, snapshots.Count));
            return (refreshed, missingIds);
        });

        progress.Report(new LibraryMaintenanceProgress("Updating the library…"));
        var byId = _tracks.ToDictionary(track => track.Id);
        foreach (var updated in scan.refreshed)
        {
            if (!byId.TryGetValue(updated.Id, out var track)) continue;
            ApplyRefreshedTrack(track, updated);
        }

        var missing = scan.missingIds.Select(id => byId.GetValueOrDefault(id)).OfType<Track>().ToList();
        foreach (var track in missing)
        {
            _tracks.Remove(track);
            foreach (var playlist in _playlists)
                playlist.TrackIds.Remove(track.Id);
            _syncedTrackIds.Remove(track.Id);
        }
        progress.Report(new LibraryMaintenanceProgress("Saving the library…"));
        await SaveMaintenanceSnapshotAsync();
        RenderLibrary();
        PlaylistList.Items.Refresh();
        PlaybackStatus.Text = missing.Count > 0
            ? $"Refreshed library — removed {missing.Count} missing track{(missing.Count == 1 ? "" : "s")}"
            : "Refreshed library";
        progress.Report(new LibraryMaintenanceProgress("Library refresh complete", snapshots.Count, snapshots.Count));
        return _tracks.Count;
    }

    private async Task SaveMaintenanceSnapshotAsync()
    {
        // Snapshot observable UI collections before crossing threads. JSON
        // serialization and the disk write can then run without enumerating a
        // collection that WPF or another completion callback might mutate.
        var tracks = _tracks.Select(MaintenanceCopy).ToList();
        var playlists = _playlists.Select(playlist =>
        {
            var copy = new Playlist { Id = playlist.Id, Name = playlist.Name };
            foreach (var id in playlist.TrackIds) copy.TrackIds.Add(id);
            return copy;
        }).ToList();
        var syncedTrackIds = _syncedTrackIds.ToList();
        await Task.Run(() => LibraryStore.Save(tracks, playlists, syncedTrackIds));
    }

    private static Track MaintenanceCopy(Track track) => new()
    {
        Id = track.Id,
        Title = track.Title,
        Artist = track.Artist,
        Album = track.Album,
        Genre = track.Genre,
        FileName = track.FileName,
        FilePath = track.FilePath,
        TrackNumber = track.TrackNumber,
        Year = track.Year,
        Duration = track.Duration,
        ExcludedFromShuffle = track.ExcludedFromShuffle,
        LastSyncedKey = track.LastSyncedKey,
        IsFavorite = track.IsFavorite,
        SyncedIpodPlayCounts = track.SyncedIpodPlayCounts is null
            ? null
            : new Dictionary<string, int>(track.SyncedIpodPlayCounts),
        ArtworkPath = track.ArtworkPath,
        PlayCount = track.PlayCount,
    };

    private static void ApplyRefreshedTrack(Track target, Track updated)
    {
        target.Title = updated.Title;
        target.Artist = updated.Artist;
        target.Album = updated.Album;
        target.Genre = updated.Genre;
        target.TrackNumber = updated.TrackNumber;
        target.Year = updated.Year;
        target.ArtworkPath = updated.ArtworkPath;
    }

    private void ExportLibrary()
    {
        SaveLibrary();
        var dialog = new SaveFileDialog
        {
            Title = "Export library",
            FileName = "sink-library.json",
            Filter = "Library JSON (*.json)|*.json",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.Copy(LibraryStore.LibraryPath, dialog.FileName, overwrite: true);
            PlaybackStatus.Text = $"Exported library to {dialog.FileName}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ImportLibrary()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import library",
            Filter = "Library JSON (*.json)|*.json|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;

        LibraryData? data;
        try
        {
            data = System.Text.Json.JsonSerializer.Deserialize<LibraryData>(File.ReadAllText(dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            MessageBox.Show(this, ex.Message, "Import failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (data is null) return;

        if (MessageBox.Show(this, $"Replace the current library with {data.Tracks.Count} tracks from this file?",
                "Import library", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        _tracks.Clear();
        _playlists.Clear();
        _syncedTrackIds.Clear();
        foreach (var track in data.Tracks) _tracks.Add(track);
        foreach (var playlist in data.Playlists)
        {
            var restored = new Playlist { Name = playlist.Name, Id = playlist.Id };
            foreach (var id in playlist.TrackIds) restored.TrackIds.Add(id);
            _playlists.Add(restored);
        }
        var known = _tracks.Select(t => t.Id).ToHashSet();
        foreach (var id in data.SyncedTrackIds.Where(known.Contains)) _syncedTrackIds.Add(id);

        SaveLibrary();
        PlaylistList.Items.Refresh();
        RenderLibrary();
        PlaybackStatus.Text = $"Imported {_tracks.Count} tracks";
    }
}
