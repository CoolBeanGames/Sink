using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sink.Services;

public enum ImportMode
{
    /// <summary>Keep tracks where they are and just remember the path (original behaviour).</summary>
    Reference,
    /// <summary>Copy imported files into the library folder.</summary>
    Copy,
    /// <summary>Move imported files into the library folder.</summary>
    Move,
}

/// <summary>
/// User preferences, persisted as %AppData%/Sink/settings.json. Loaded once at
/// startup into <see cref="Current"/>; call <see cref="Save"/> after changing it.
/// </summary>
public sealed class AppSettings
{
    private static readonly string Path_ = Path.Combine(LibraryStore.Directory, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Where downloaded and copied/moved tracks live.</summary>
    public string LibraryLocation { get; set; } = Path.Combine(LibraryStore.Directory, "Library");

    /// <summary>Where downloaded podcast episodes live.</summary>
    public string PodcastLocation { get; set; } = Path.Combine(LibraryStore.Directory, "Podcasts");

    /// <summary>Start a sync as soon as an iPod is connected.</summary>
    public bool SyncOnConnect { get; set; }

    public ImportMode ImportMode { get; set; } = ImportMode.Reference;

    /// <summary>
    /// Which browser's cookies yt-dlp borrows to get past YouTube's "confirm
    /// you're not a bot" wall: "auto" (detect an installed browser), "none",
    /// "file" (a manually-supplied cookies.txt, see <see cref="CookieFilePath"/>),
    /// or an explicit yt-dlp browser name (edge, chrome, firefox, brave, …).
    /// </summary>
    public string YouTubeCookies { get; set; } = "auto";

    /// <summary>
    /// Path to a Netscape-format cookies.txt used when YouTubeCookies is "file" —
    /// either hand-exported by the user, or written by "Export Firefox
    /// cookies…" in Settings. Sidesteps live browser-cookie extraction
    /// entirely (Chrome/Edge's DPAPI-encrypted store, a locked profile while
    /// the browser is open, etc.) since it's just a static file yt-dlp reads
    /// with --cookies.
    /// </summary>
    public string CookieFilePath { get; set; } = "";

    [JsonIgnore]
    public static AppSettings Current { get; private set; } = new();

    public static void Load()
    {
        try
        {
            if (File.Exists(Path_))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path_), Options) ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Current = new AppSettings();
        }
    }

    public void Save()
    {
        Current = this;
        try
        {
            Directory.CreateDirectory(LibraryStore.Directory);
            File.WriteAllText(Path_, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public AppSettings Clone() => new()
    {
        LibraryLocation = LibraryLocation,
        PodcastLocation = PodcastLocation,
        SyncOnConnect = SyncOnConnect,
        ImportMode = ImportMode,
        YouTubeCookies = YouTubeCookies,
        CookieFilePath = CookieFilePath,
    };
}
