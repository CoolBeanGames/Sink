using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Sink.Services.Download;

public enum DownloadState { Pending, Scanning, Ready, Downloading, Importing, Done, Failed }

/// <summary>Where a node sits in the download tree.</summary>
public enum DownloadKind { Single, Album, Artist, Track }

/// <summary>
/// One node in the download tree. An artist link becomes an <see cref="DownloadKind.Artist"/>
/// node with <see cref="DownloadKind.Album"/> children, each of which expands to
/// <see cref="DownloadKind.Track"/> children. A plain album link is a root Album
/// node; a single video is a <see cref="DownloadKind.Single"/> node with no children.
/// Every node carries an include toggle; a parent's toggle cascades to its children
/// and reads back as indeterminate when they disagree.
/// </summary>
public sealed class DownloadNode : INotifyPropertyChanged
{
    private string _url = "";
    private string _title = "";
    private string _artist = "";
    private string _album = "";
    private string _genre = "";
    private bool _enabled = true;
    private bool _isExpanded;
    private bool _scanned;
    private DownloadState _state = DownloadState.Pending;
    private double _progress;
    private string _statusText = "Waiting to scan";

    public DownloadNode(DownloadKind kind)
    {
        Kind = kind;
        Children.CollectionChanged += (_, e) =>
        {
            foreach (var added in e.NewItems?.OfType<DownloadNode>() ?? [])
            {
                added.Parent = this;
                added.PropertyChanged += Child_PropertyChanged;
            }
            OnChildChanged();
            OnPropertyChanged(nameof(HasChildren));
        };
    }

    public DownloadKind Kind { get; }
    public DownloadNode? Parent { get; private set; }
    public ObservableCollection<DownloadNode> Children { get; } = [];

    /// <summary>1-based position in the source playlist (Track nodes only).</summary>
    public int Index { get; init; }
    public string ScannedTitle { get; init; } = "";

    /// <summary>Whether this node's child list has been fetched yet.</summary>
    public bool Scanned { get => _scanned; set => Set(ref _scanned, value); }

    public string Url { get => _url; set => Set(ref _url, value); }

    public string Title
    {
        get => _title;
        set { if (Set(ref _title, value)) { OnPropertyChanged(nameof(TitleEdited)); OnPropertyChanged(nameof(Name)); } }
    }

    public string Artist
    {
        get => _artist;
        set
        {
            if (!Set(ref _artist, value)) return;
            OnPropertyChanged(nameof(Name));
            if (Kind == DownloadKind.Artist)
                foreach (var c in Children) c.Artist = value;
        }
    }

    public string Album
    {
        get => _album;
        set { if (Set(ref _album, value)) OnPropertyChanged(nameof(Name)); }
    }

    public string Genre
    {
        get => _genre;
        set
        {
            if (!Set(ref _genre, value)) return;
            if (Kind == DownloadKind.Artist)
                foreach (var c in Children) c.Genre = value;
        }
    }

    /// <summary>The node's primary editable label — artist name, album name or track title depending on kind.</summary>
    public string Name
    {
        get => Kind switch
        {
            DownloadKind.Artist => _artist,
            DownloadKind.Album => _album,
            _ => _title
        };
        set
        {
            switch (Kind)
            {
                case DownloadKind.Artist: Artist = value; break;
                case DownloadKind.Album: Album = value; break;
                default: Title = value; break;
            }
        }
    }

    // Which secondary fields this kind exposes.
    public bool IsTrack => Kind == DownloadKind.Track;
    public bool ShowArtist => Kind == DownloadKind.Single || (Kind == DownloadKind.Album && Parent is null);
    public bool ShowAlbum => Kind == DownloadKind.Single;
    public bool ShowGenre => Kind is DownloadKind.Single or DownloadKind.Artist
                             || (Kind == DownloadKind.Album && Parent is null);

    /// <summary>Include toggle. Null = children disagree.</summary>
    public bool? Enabled
    {
        get
        {
            if (Children.Count == 0) return _enabled;
            bool? acc = null;
            foreach (var c in Children)
            {
                var v = c.Enabled;
                if (acc is null) { acc = v; continue; }
                if (acc != v) return null;
            }
            return acc;
        }
        set
        {
            var v = value ?? true;
            if (Children.Count == 0)
            {
                if (_enabled == v) return;
                _enabled = v;
                OnPropertyChanged(nameof(Enabled));
            }
            else
            {
                foreach (var c in Children) c.Enabled = v;
            }
            Parent?.OnChildChanged();
        }
    }

    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }
    public bool HasChildren => Children.Count > 0;
    public bool CanHaveChildren => Kind is DownloadKind.Artist or DownloadKind.Album;

    public DownloadState State
    {
        get => _state;
        set { if (Set(ref _state, value)) OnPropertyChanged(nameof(IsFinished)); }
    }

    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    public bool IsFinished => _state is DownloadState.Done or DownloadState.Failed;

    /// <summary>True when a Track's title was hand-edited away from what yt-dlp reported.</summary>
    public bool TitleEdited => Kind == DownloadKind.Track
        && _title.Trim().Length > 0
        && !string.Equals(_title.Trim(), ScannedTitle.Trim(), StringComparison.Ordinal);

    /// <summary>Enumerates this node and every descendant, depth-first.</summary>
    public IEnumerable<DownloadNode> SelfAndDescendants()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var node in child.SelfAndDescendants())
                yield return node;
    }

    private void Child_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Enabled)) OnChildChanged();
    }

    private void OnChildChanged()
    {
        OnPropertyChanged(nameof(Enabled));
        Parent?.OnChildChanged();
    }

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
