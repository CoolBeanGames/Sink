using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sink.Models;

namespace Sink.Services;

public sealed class PlaylistData
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Playlist";
    public List<Guid> TrackIds { get; set; } = [];
}

public sealed class LibraryData
{
    public List<Track> Tracks { get; set; } = [];
    public List<PlaylistData> Playlists { get; set; } = [];
    public List<Guid> SyncedTrackIds { get; set; } = [];
}

/// <summary>
/// Persists the library as JSON under %AppData%/Sink. Tracks currently store a
/// link (FilePath) to the source file; copy-in support will reuse this store.
/// </summary>
public static class LibraryStore
{
    public static string Directory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sink");

    public static string LibraryPath { get; } = Path.Combine(Directory, "library.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static LibraryData? Load()
    {
        try
        {
            if (!File.Exists(LibraryPath)) return null;
            return JsonSerializer.Deserialize<LibraryData>(File.ReadAllText(LibraryPath), Options);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Save(IEnumerable<Track> tracks, IEnumerable<Playlist> playlists, IEnumerable<Guid> syncedTrackIds)
    {
        var data = new LibraryData
        {
            Tracks = tracks.ToList(),
            Playlists = playlists.Select(p => new PlaylistData
            {
                Id = p.Id,
                Name = p.Name,
                TrackIds = p.TrackIds.ToList()
            }).ToList(),
            SyncedTrackIds = syncedTrackIds.ToList()
        };
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(LibraryPath, JsonSerializer.Serialize(data, Options));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
