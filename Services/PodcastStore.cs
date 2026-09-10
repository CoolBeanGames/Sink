using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sink.Models;

namespace Sink.Services;

/// <summary>Persists subscribed podcasts and their episode play-state to %AppData%/Sink/podcasts.json.</summary>
public static class PodcastStore
{
    private static readonly string Path_ = System.IO.Path.Combine(LibraryStore.Directory, "podcasts.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static List<Podcast> Load()
    {
        try
        {
            if (File.Exists(Path_))
                return JsonSerializer.Deserialize<List<Podcast>>(File.ReadAllText(Path_), Options) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return [];
    }

    public static void Save(IEnumerable<Podcast> podcasts)
    {
        try
        {
            Directory.CreateDirectory(LibraryStore.Directory);
            File.WriteAllText(Path_, JsonSerializer.Serialize(podcasts.ToList(), Options));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
