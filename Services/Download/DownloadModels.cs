using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

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
    private int _trackNumber;
    private string? _artworkOverride;
    private DownloadState _state = DownloadState.Pending;
    private double _progress;
    private string _statusText = "Waiting to scan";
    private bool _translating;

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

    /// <summary>
    /// True on an Album-kind node that came from a scanned yt-dlp *playlist*
    /// rather than a genuine single-artist album — a personal playlist mixes
    /// tracks from different artists/albums, so unlike a real album it must
    /// not force every track to share the playlist's own name as its Album
    /// tag (task 151).
    /// </summary>
    public bool IsMixedPlaylist { get; init; }

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

    /// <summary>Track number written on import (Track and Single nodes).</summary>
    public int TrackNumber
    {
        get => _trackNumber;
        set { if (Set(ref _trackNumber, value)) OnPropertyChanged(nameof(TrackNumberText)); }
    }

    /// <summary>Bound to the little number box; empty string clears it.</summary>
    public string TrackNumberText
    {
        get => _trackNumber > 0 ? _trackNumber.ToString() : "";
        set => TrackNumber = int.TryParse(value?.Trim(), out var n) && n > 0 ? n : 0;
    }

    /// <summary>User-supplied cover image path; overrides yt-dlp's embed for the whole album / single.</summary>
    public string? ArtworkOverride
    {
        get => _artworkOverride;
        set { if (Set(ref _artworkOverride, value)) OnPropertyChanged(nameof(HasArtworkOverride)); }
    }

    public bool HasArtworkOverride => !string.IsNullOrWhiteSpace(_artworkOverride);

    // Which secondary fields this kind exposes.
    public bool IsTrack => Kind == DownloadKind.Track;
    public bool ShowTrackNumber => Kind is DownloadKind.Track or DownloadKind.Single;
    public bool ShowArtist => Kind == DownloadKind.Single || (Kind == DownloadKind.Album && Parent is null)
                              || (Kind == DownloadKind.Track && Parent?.IsMixedPlaylist == true);
    public bool ShowAlbum => Kind == DownloadKind.Single || (Kind == DownloadKind.Track && Parent?.IsMixedPlaylist == true);
    public bool ShowGenre => Kind is DownloadKind.Single or DownloadKind.Artist
                             || (Kind == DownloadKind.Album && Parent is null)
                             || (Kind == DownloadKind.Track && Parent?.IsMixedPlaylist == true);

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
            NotifyStatus();
            Parent?.OnChildChanged();
        }
    }

    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }
    public bool HasChildren => Children.Count > 0;
    public bool CanHaveChildren => Kind is DownloadKind.Artist or DownloadKind.Album;

    public DownloadState State
    {
        get => _state;
        set { if (Set(ref _state, value)) { OnPropertyChanged(nameof(IsFinished)); OnPropertyChanged(nameof(IsBusy)); NotifyStatus(); } }
    }

    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    public bool IsFinished => _state is DownloadState.Done or DownloadState.Failed;

    /// <summary>True while this row is fetching its English title (task 111).</summary>
    public bool Translating
    {
        get => _translating;
        set { if (Set(ref _translating, value)) OnPropertyChanged(nameof(IsBusy)); }
    }

    /// <summary>Drives the per-row spinner: a download or a title translation is in flight.</summary>
    public bool IsBusy => _translating || _state is DownloadState.Downloading or DownloadState.Importing;

    // ---- Status glyph (task 110) --------------------------------------
    //
    // A small light on every row. Green downloaded, blue downloading, red
    // failed, orange armed-and-selected, grey scanning, black off. Everything
    // but the black / grey states glows. Clicking the light toggles the row
    // between black (won't download) and orange (will).

    private const string GlyphGreen = "#3FB950";
    private const string GlyphBlue = "#4C8DFF";
    private const string GlyphRed = "#F85149";
    private const string GlyphOrange = "#F0883E";
    private const string GlyphGrey = "#6E7584";
    private const string GlyphBlack = "#0C0D11";

    /// <summary>
    /// An artist row isn't itself a download unit — only its album children are
    /// (see <c>DownloadUnits</c>) — so its own <see cref="_state"/> never moves.
    /// Roll the albums' states up so the artist's light actually reflects what's
    /// happening underneath it (task 139).
    /// </summary>
    private DownloadState EffectiveState
    {
        get
        {
            if (Kind != DownloadKind.Artist || Children.Count == 0) return _state;
            if (Children.Any(c => c.EffectiveState is DownloadState.Downloading or DownloadState.Importing)) return DownloadState.Downloading;
            if (Children.Any(c => c.EffectiveState == DownloadState.Scanning)) return DownloadState.Scanning;
            if (Children.Any(c => c.EffectiveState == DownloadState.Failed)) return DownloadState.Failed;
            if (Children.All(c => c.EffectiveState == DownloadState.Done)) return DownloadState.Done;
            return _state;
        }
    }

    private string GlyphHex => EffectiveState switch
    {
        DownloadState.Done => GlyphGreen,
        DownloadState.Failed => GlyphRed,
        DownloadState.Downloading or DownloadState.Importing => GlyphBlue,
        DownloadState.Scanning or DownloadState.Pending => GlyphGrey,
        _ => Enabled == false ? GlyphBlack : GlyphOrange,
    };

    private bool GlyphGlows => EffectiveState switch
    {
        DownloadState.Done or DownloadState.Failed or DownloadState.Downloading or DownloadState.Importing => true,
        DownloadState.Scanning or DownloadState.Pending => false,
        _ => Enabled == true,
    };

    public Brush StatusBrush
    {
        get
        {
            var brush = new SolidColorBrush(ParseColor(GlyphHex));
            brush.Freeze();
            return brush;
        }
    }

    public Color StatusGlowColor => ParseColor(GlyphHex);
    public double StatusGlowOpacity => GlyphGlows ? 0.95 : 0.0;
    public double StatusGlowRadius => GlyphGlows ? 13 : 0;

    public string StatusGlyphTooltip => EffectiveState switch
    {
        DownloadState.Done => "Downloaded",
        DownloadState.Failed => "Download failed",
        DownloadState.Downloading or DownloadState.Importing => "Downloading…",
        DownloadState.Scanning => "Scanning…",
        DownloadState.Pending => "Waiting to scan",
        _ => Enabled == false ? "Off — click to queue for download" : "Queued — click to skip",
    };

    private static Color ParseColor(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    private void NotifyStatus()
    {
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(StatusGlowColor));
        OnPropertyChanged(nameof(StatusGlowOpacity));
        OnPropertyChanged(nameof(StatusGlowRadius));
        OnPropertyChanged(nameof(StatusGlyphTooltip));
    }

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
        if (e.PropertyName is nameof(Enabled) or nameof(State)) OnChildChanged();
    }

    private void OnChildChanged()
    {
        OnPropertyChanged(nameof(Enabled));
        NotifyStatus();
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

    /// <summary>Write each album track's playlist position as its track number when it has none of its own.</summary>
    public bool NumberTracks { get; set; }
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
