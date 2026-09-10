using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Sink.Services.Download;

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
