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
        var dialog = new SettingsWindow(AppSettings.Current.Clone(), RefreshLibrary, ExportLibrary, ImportLibrary) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;
        dialog.Result.Save();
        PlaybackStatus.Text = "Settings saved";
    }

    /// <summary>Re-reads tags/art for every track and drops any whose file is gone. Returns the number kept.</summary>
    private int RefreshLibrary()
    {
        var missing = _tracks.Where(t => !MusicImporter.RefreshTags(t)).ToList();
        foreach (var track in missing)
        {
            _tracks.Remove(track);
            foreach (var playlist in _playlists)
                playlist.TrackIds.Remove(track.Id);
            _syncedTrackIds.Remove(track.Id);
        }
        SaveLibrary();
        RenderLibrary();
        PlaylistList.Items.Refresh();
        PlaybackStatus.Text = missing.Count > 0
            ? $"Refreshed library — removed {missing.Count} missing track{(missing.Count == 1 ? "" : "s")}"
            : "Refreshed library";
        return _tracks.Count;
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
