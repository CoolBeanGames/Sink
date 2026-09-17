using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Sink.Dialogs;
using Sink.Models;
using Sink.Services;

namespace Sink;

/// <summary>
/// The Tags page (task 126): a flat, sortable, searchable grid of every song
/// in the library with the same editing capabilities as the download page
/// (inline field edits, bulk metadata editing, translation, cover art).
/// Unlike the download tree this is never hierarchical/collapsible — every
/// track is its own row.
/// </summary>
public partial class MainWindow
{
    private bool _tagsViewActive;
    private ListCollectionView? _tagsView;
    private string _tagsSearch = "";
    private bool _tagsDirty;

    private void InitTagsPage()
    {
        _tagsView = new ListCollectionView(_tracks) { Filter = TagsFilter };
        TagsGrid.ItemsSource = _tagsView;
    }

    private bool TagsFilter(object obj)
    {
        if (string.IsNullOrWhiteSpace(_tagsSearch)) return true;
        return obj is Track t &&
            (t.Title.Contains(_tagsSearch, StringComparison.OrdinalIgnoreCase)
             || t.Artist.Contains(_tagsSearch, StringComparison.OrdinalIgnoreCase)
             || t.Album.Contains(_tagsSearch, StringComparison.OrdinalIgnoreCase)
             || t.Genre.Contains(_tagsSearch, StringComparison.OrdinalIgnoreCase));
    }

    private void TagsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _tagsSearch = TagsSearchBox.Text.Trim();
        _tagsView?.Refresh();
        UpdateTagsSubtitle();
    }

    private void UpdateTagsSubtitle()
    {
        var shown = _tagsView?.Cast<object>().Count() ?? _tracks.Count;
        TagsSubtitle.Text = string.IsNullOrWhiteSpace(_tagsSearch)
            ? $"{_tracks.Count} track{(_tracks.Count == 1 ? "" : "s")}"
            : $"{shown} of {_tracks.Count} track{(_tracks.Count == 1 ? "" : "s")} match \"{_tagsSearch}\"";
    }

    // ---- View switching -------------------------------------------------

    private void ShowTagsSource_Click(object sender, RoutedEventArgs e)
    {
        ExitDownloadView();
        ExitPodcastView();
        ExitReflectView();
        _tagsViewActive = true;
        MusicPage.Visibility = Visibility.Collapsed;
        DownloadPage.Visibility = Visibility.Collapsed;
        PodcastPage.Visibility = Visibility.Collapsed;
        IpodCanvas.Visibility = Visibility.Collapsed;
        TagsPage.Visibility = Visibility.Visible;

        SetNavExpanded(MusicNav, MusicNavTransform, false);
        SetNavExpanded(IpodNav, IpodNavTransform, false);
        MusicHeaderButton.Tag = null;
        IpodHeaderButton.Tag = null;
        DownloadHeaderButton.Tag = null;
        TagsHeaderButton.Tag = "Active";
        SetActiveNavigation(null);

        UpdateTagsSubtitle();
    }

    private void ExitTagsView()
    {
        if (!_tagsViewActive) return;
        FlushTagsIfDirty();
        _tagsViewActive = false;
        TagsPage.Visibility = Visibility.Collapsed;
        MusicPage.Visibility = Visibility.Visible;
        IpodCanvas.Visibility = Visibility.Visible;
        TagsHeaderButton.Tag = null;
    }

    // ---- Editing + saving -------------------------------------------------
    //
    // A field's binding already commits into the Track object on LostFocus;
    // "saved" here means flushed to library.json — done when the selection
    // moves on (including growing the selection) or the user hits Ctrl+S,
    // per the task's spec, not on every keystroke.

    private void TagsField_LostFocus(object sender, RoutedEventArgs e) => _tagsDirty = true;

    private void TagsGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            FlushTagsIfDirty();
            e.Handled = true;
        }
    }

    private void FlushTagsIfDirty()
    {
        if (!_tagsDirty) return;
        _tagsDirty = false;
        SaveLibrary();
    }

    private void TagsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        FlushTagsIfDirty();
        var count = TagsGrid.SelectedItems.Count;
        TagsSelectionBar.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        TagsSelectionText.Text = $"{count} selected";
    }

    private void TagsClearSelection_Click(object sender, RoutedEventArgs e) => TagsGrid.SelectedItems.Clear();

    // ---- Right-click menu -------------------------------------------------

    private void TagsGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var tracks = TagsGrid.SelectedItems.OfType<Track>().ToList();
        if (tracks.Count == 0) { e.Handled = true; return; }

        var menu = TagsGrid.ContextMenu!;
        menu.Items.Clear();
        menu.Items.Add(Header(tracks.Count == 1 ? tracks[0].Title : $"{tracks.Count} tracks"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Play", () => PlayTracks(tracks)));
        menu.Items.Add(Item(tracks.Any(t => !t.IsFavorite) ? "♥ Favorite" : "♡ Remove favorite", () => { ToggleFavorite(tracks); }));
        menu.Items.Add(Item("Edit metadata…", () => EditTagsMetadata(tracks)));
        menu.Items.Add(Item("Trim titles…", () => TrimTagsTrackTitles(tracks)));
        menu.Items.Add(Item(tracks.Count == 1 ? "Translate title to English" : "Translate titles to English", () => _ = TranslateTracksAsync(tracks)));
        menu.Items.Add(Item(tracks.Count == 1 ? "Auto-tag" : "Auto-tag all", () => _ = AutoTagTracksAsync(tracks)));
        menu.Items.Add(Item("Set cover art…", () => SetTagsArtwork(tracks)));
        menu.Items.Add(Item("Crop album art", () => CropTagsArtwork(tracks)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Delete from library", () => { FlushTagsIfDirty(); DeleteTracks(tracks); }));
    }

    /// <summary>Mirrors the download page's title-trimming tool (task 159), reusing the same dialog via <see cref="ITitleTrimmable"/>.</summary>
    private void TrimTagsTrackTitles(IReadOnlyList<Track> tracks)
    {
        FlushTagsIfDirty();
        var dialog = new TrimTitlesWindow(tracks) { Owner = this };
        BlurBehind(true);
        var ok = dialog.ShowDialog() == true;
        BlurBehind(false);
        if (!ok) return;
        SaveLibrary();
        _tagsView?.Refresh();
        PlaybackStatus.Text = $"Trimmed {tracks.Count} track title{(tracks.Count == 1 ? "" : "s")}";
    }

    private void EditTagsMetadata(IReadOnlyList<Track> tracks)
    {
        FlushTagsIfDirty();
        var dialog = new MetadataWindow(tracks) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        SaveLibrary();
        _tagsView?.Refresh();
        PlaybackStatus.Text = $"Updated {tracks.Count} track{(tracks.Count == 1 ? "" : "s")}";
    }

    /// <summary>Mirrors the download page's per-title translation (task 111): throttled, one at a time, with a retry pass for transient failures.</summary>
    private async Task TranslateTracksAsync(IReadOnlyList<Track> tracks)
    {
        FlushTagsIfDirty();
        var changed = 0;
        var failed = 0;
        var pending = new List<Track>();
        for (var i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            try
            {
                var result = await Translation.ToEnglishAsync(track.Title);
                if (!result.Ok) { failed++; pending.Add(track); }
                else if (result.Changed) { track.Title = result.Text; changed++; }
            }
            catch (Exception ex)
            {
                failed++;
                pending.Add(track);
                Log.Warn($"Translate failed for \"{track.Title}\": {ex.Message}");
            }
            if (tracks.Count > 1) PlaybackStatus.Text = $"Translating… {i + 1}/{tracks.Count}";
            if (i < tracks.Count - 1) await Task.Delay(150);
        }
        if (pending.Count > 0)
        {
            foreach (var track in pending)
            {
                await Task.Delay(500);
                try
                {
                    var result = await Translation.ToEnglishAsync(track.Title);
                    if (result.Ok) { failed--; if (result.Changed) { track.Title = result.Text; changed++; } }
                }
                catch (Exception ex) { Log.Warn($"Translate retry failed for \"{track.Title}\": {ex.Message}"); }
            }
        }
        if (changed > 0) SaveLibrary();
        _tagsView?.Refresh();
        PlaybackStatus.Text = changed == 0
            ? (failed == 0 ? "Titles are already English" : "Couldn't reach the translation service")
            : $"Translated {changed} title{(changed == 1 ? "" : "s")} to English" + (failed > 0 ? $" · {failed} still failed" : "");
    }

    /// <summary>
    /// Fills in Album/Genre/Artist/Year via <see cref="AutoTagService"/> —
    /// only touches fields still holding the app's own "unset" placeholder,
    /// so a real (even if imperfect) existing value is never clobbered
    /// (task 160).
    /// </summary>
    private async Task AutoTagTracksAsync(IReadOnlyList<Track> tracks)
    {
        FlushTagsIfDirty();
        var updated = 0;
        var notFound = 0;
        for (var i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            if (tracks.Count > 1) PlaybackStatus.Text = $"Auto-tagging… {i + 1}/{tracks.Count}";
            var result = await AutoTagService.LookupAsync(track.Title, track.Artist);
            if (!result.Found) { notFound++; continue; }

            var changed = false;
            if (AutoTagService.IsUnknown(track.Album, "Unknown Album") && !string.IsNullOrWhiteSpace(result.Album))
            { track.Album = result.Album!; changed = true; }
            if (AutoTagService.IsUnknown(track.Genre, "Unknown") && !string.IsNullOrWhiteSpace(result.Genre))
            { track.Genre = result.Genre!; changed = true; }
            if (AutoTagService.IsUnknown(track.Artist, "Unknown Artist") && !string.IsNullOrWhiteSpace(result.Artist))
            { track.Artist = result.Artist!; changed = true; }
            if (track.Year == 0 && result.Year > 0) { track.Year = result.Year; changed = true; }
            if (track.TrackNumber == 0 && result.TrackNumber > 0) { track.TrackNumber = result.TrackNumber; changed = true; }
            if (changed) updated++;
        }
        if (updated > 0) SaveLibrary();
        _tagsView?.Refresh();
        PlaybackStatus.Text = updated == 0
            ? "Nothing to fill in — no matches, or metadata already complete"
            : $"Auto-tagged {updated} track{(updated == 1 ? "" : "s")}" + (notFound > 0 ? $" · {notFound} not found" : "");
    }

    private void SetTagsArtwork(IReadOnlyList<Track> tracks)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose cover art",
            Filter = "Images|*.jpg;*.jpeg;*.png;*.webp;*.bmp|All files|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        var bytes = Artwork.SquareCropBytes(dialog.FileName);
        if (bytes is null) { PlaybackStatus.Text = "Couldn't read that image"; return; }
        foreach (var track in tracks)
        {
            var path = Artwork.Save($"track:{track.Id}", bytes, "image/jpeg");
            if (path is not null) track.ArtworkPath = path;
        }
        _artCache.Clear();
        SaveLibrary();
        RenderLibrary();
        PlaybackStatus.Text = $"Cover art set for {tracks.Count} track{(tracks.Count == 1 ? "" : "s")}";
    }

    private void CropTagsArtwork(IReadOnlyList<Track> tracks)
    {
        var cropped = 0;
        foreach (var track in tracks)
        {
            if (!string.IsNullOrWhiteSpace(track.ArtworkPath) && File.Exists(track.ArtworkPath))
            {
                if (Artwork.Recrop(track.ArtworkPath)) cropped++;
            }
            else if (!string.IsNullOrWhiteSpace(track.FilePath) && File.Exists(track.FilePath))
            {
                var path = Artwork.ExtractAndCrop(track.FilePath, $"track:{track.Id}");
                if (path is not null) { track.ArtworkPath = path; cropped++; }
            }
        }
        if (cropped == 0) { PlaybackStatus.Text = "No cover art to crop"; return; }
        _artCache.Clear();
        SaveLibrary();
        RenderLibrary();
        PlaybackStatus.Text = $"Cropped cover art for {cropped} track{(cropped == 1 ? "" : "s")}";
    }
}
