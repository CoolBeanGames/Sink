using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Sink.Services.Download;

/// <summary>
/// One track inside an album / playlist download row: its (editable) title and
/// whether it should be included in the download. <see cref="Index"/> is the
/// 1-based position in the source playlist, used for <c>--playlist-items</c>.
/// </summary>
public sealed class TrackChoice : INotifyPropertyChanged
{
    private bool _enabled = true;
    private string _title;

    public TrackChoice(int index, string title)
    {
        Index = index;
        ScannedTitle = title;
        _title = title;
    }

    public int Index { get; }
    public string ScannedTitle { get; }

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public string Title { get => _title; set => Set(ref _title, value); }

    /// <summary>True when the user has changed the title away from what yt-dlp reported.</summary>
    public bool TitleEdited =>
        _title.Trim().Length > 0 && !string.Equals(_title.Trim(), ScannedTitle.Trim(), StringComparison.Ordinal);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public enum DownloadState { Pending, Scanning, Ready, Downloading, Importing, Done, Failed }

/// <summary>
/// One row in the download list: the source link plus the metadata we scanned
/// (or the user edited). Implements <see cref="INotifyPropertyChanged"/> so the
/// grid updates live as scanning and downloading progress.
/// </summary>
public sealed class DownloadItem : INotifyPropertyChanged
{
    private string _url = "";
    private string _title = "";
    private string _artist = "";
    private string _album = "";
    private string _genre = "";
    private DownloadState _state = DownloadState.Pending;
    private double _progress;
    private string _statusText = "Waiting to scan";
    private bool _isPlaylist;
    private int _trackCount;
    private bool _enabled = true;
    private bool _isExpanded;

    public DownloadItem()
    {
        Tracks.CollectionChanged += (_, e) =>
        {
            foreach (var added in e.NewItems?.OfType<TrackChoice>() ?? [])
                added.PropertyChanged += (_, _) => OnPropertyChanged(nameof(TrackSummary));
            OnPropertyChanged(nameof(HasTrackList));
            OnPropertyChanged(nameof(TrackSummary));
        };
    }

    /// <summary>Child tracks for an album / playlist row; empty for a single track.</summary>
    public ObservableCollection<TrackChoice> Tracks { get; } = [];

    public bool HasTrackList => Tracks.Count > 0;

    public string TrackSummary => Tracks.Count == 0
        ? ""
        : $"{Tracks.Count(t => t.Enabled)}/{Tracks.Count} tracks";

    /// <summary>Whether this row is part of the next download run.</summary>
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    /// <summary>Whether the row's track list is shown.</summary>
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    public string Url { get => _url; set => Set(ref _url, value); }
    public string Title { get => _title; set => Set(ref _title, value); }
    public string Artist { get => _artist; set => Set(ref _artist, value); }
    public string Album { get => _album; set => Set(ref _album, value); }
    public string Genre { get => _genre; set => Set(ref _genre, value); }

    public DownloadState State
    {
        get => _state;
        set { if (Set(ref _state, value)) OnPropertyChanged(nameof(IsFinished)); }
    }

    /// <summary>0..1 download progress for this item.</summary>
    public double Progress { get => _progress; set => Set(ref _progress, value); }

    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    /// <summary>True when the link is a playlist / album — one row, many tracks.</summary>
    public bool IsPlaylist { get => _isPlaylist; set => Set(ref _isPlaylist, value); }

    /// <summary>Number of tracks the link resolves to (1 for a single video).</summary>
    public int TrackCount { get => _trackCount; set => Set(ref _trackCount, value); }

    public bool IsFinished => _state is DownloadState.Done or DownloadState.Failed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public enum AudioFormat { Mp3, M4a, Opus, Flac, Wav }

/// <summary>User-chosen download settings, surfaced in the options panel.</summary>
public sealed class DownloadOptions
{
    public bool WriteMetadata { get; set; } = true;
    public bool EmbedAlbumArt { get; set; } = true;
    public bool PreferMusicMetadata { get; set; } = true;
    public AudioFormat Format { get; set; } = AudioFormat.Mp3;

    /// <summary>Audio bitrate in kbps, or 0 for "best available".</summary>
    public int Quality { get; set; } = 0;

    public string FormatExtension => Format switch
    {
        AudioFormat.Mp3 => "mp3",
        AudioFormat.M4a => "m4a",
        AudioFormat.Opus => "opus",
        AudioFormat.Flac => "flac",
        AudioFormat.Wav => "wav",
        _ => "mp3"
    };
}
