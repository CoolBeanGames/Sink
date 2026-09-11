using System.IO;
using System.Text.Json;
using Sink.Models;

namespace Sink.Services;

/// <summary>Persists the listen-event log that drives the Reflect page (task 127).</summary>
public static class ReflectStore
{
    private static readonly string Path_ = Path.Combine(LibraryStore.Directory, "reflect.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static List<ListenEvent> Load()
    {
        try
        {
            if (!File.Exists(Path_)) return [];
            return JsonSerializer.Deserialize<List<ListenEvent>>(File.ReadAllText(Path_), Options) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static void Save(IEnumerable<ListenEvent> events)
    {
        try
        {
            Directory.CreateDirectory(LibraryStore.Directory);
            File.WriteAllText(Path_, JsonSerializer.Serialize(events, Options));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
