using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sink.Services.Download;

/// <summary>Serializable shape of one <see cref="DownloadNode"/>, recursively.</summary>
public sealed class DownloadNodeData
{
    public DownloadKind Kind { get; set; }
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string Genre { get; set; } = "";
    public int TrackNumber { get; set; }
    public string? ArtworkOverride { get; set; }
    public bool Enabled { get; set; } = true;
    public string StatusText { get; set; } = "";
    public int Index { get; set; }
    public string ScannedTitle { get; set; } = "";
    public bool Scanned { get; set; }
    /// <summary>Whether this track already finished downloading before its album was saved as failed —
    /// preserved so a reloaded retry re-downloads only what's left instead of duplicating it (task 153).</summary>
    public bool Done { get; set; }
    /// <summary>Was missing entirely before (task 154): without it, a reload after a restart lost the
    /// "mixed playlist" flag, silently reverting every track back to the shared album Artist/Album/Genre
    /// on retry instead of each track's own edited values.</summary>
    public bool IsMixedPlaylist { get; set; }
    public List<DownloadNodeData> Children { get; set; } = [];
}

/// <summary>
/// Persists download-tree rows that failed (scan or download) so a link the
/// user pasted isn't silently lost just because Sink was closed before it was
/// retried or manually cleared (task 135).
/// </summary>
public static class DownloadQueueStore
{
    public static string Path_ { get; } = Path.Combine(LibraryStore.Directory, "download-queue.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Saves the given (already-filtered) failed root nodes. An empty list clears the file.</summary>
    public static void Save(IEnumerable<DownloadNode> failedRoots)
    {
        var data = failedRoots.Select(ToData).ToList();
        try
        {
            Directory.CreateDirectory(LibraryStore.Directory);
            if (data.Count == 0) { if (File.Exists(Path_)) File.Delete(Path_); return; }
            File.WriteAllText(Path_, JsonSerializer.Serialize(data, Options));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static List<DownloadNode> Load()
    {
        try
        {
            if (!File.Exists(Path_)) return [];
            var data = JsonSerializer.Deserialize<List<DownloadNodeData>>(File.ReadAllText(Path_), Options);
            return data?.Select(FromData).ToList() ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static DownloadNodeData ToData(DownloadNode node) => new()
    {
        Kind = node.Kind,
        Url = node.Url,
        Title = node.Title,
        Artist = node.Artist,
        Album = node.Album,
        Genre = node.Genre,
        TrackNumber = node.TrackNumber,
        ArtworkOverride = node.ArtworkOverride,
        Enabled = node.Enabled ?? true,
        StatusText = node.StatusText,
        Index = node.Index,
        ScannedTitle = node.ScannedTitle,
        Scanned = node.Scanned,
        Done = node.State == DownloadState.Done,
        IsMixedPlaylist = node.IsMixedPlaylist,
        Children = node.Children.Select(ToData).ToList(),
    };

    private static DownloadNode FromData(DownloadNodeData data)
    {
        var node = new DownloadNode(data.Kind)
        {
            IsMixedPlaylist = data.IsMixedPlaylist,
            Url = data.Url,
            Title = data.Title,
            Artist = data.Artist,
            Album = data.Album,
            Genre = data.Genre,
            TrackNumber = data.TrackNumber,
            ArtworkOverride = data.ArtworkOverride,
            Enabled = data.Enabled,
            State = data.Done ? DownloadState.Done : DownloadState.Failed,
            StatusText = data.StatusText,
            Index = data.Index,
            ScannedTitle = data.ScannedTitle,
            Scanned = data.Scanned,
        };
        foreach (var child in data.Children) node.Children.Add(FromData(child));
        return node;
    }
}
