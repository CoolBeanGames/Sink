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

    public static void Export(string path, IReadOnlyList<ListenEvent> events) => WriteTransfer(path, events);

    public static (List<ListenEvent> Events, int Added) Import(string path, IReadOnlyList<ListenEvent> current)
    {
        // The export has the same format as the local history, so an old
        // computer's reflect.json can also be imported directly.
        var incoming = JsonSerializer.Deserialize<List<ListenEvent>>(File.ReadAllText(path), Options)
            ?? throw new JsonException("This file doesn't contain Reflect history.");
        var merged = current.ToList();
        var existing = current.Select((listen, index) => (listen, index))
            .GroupBy(entry => EventKey(entry.listen))
            .ToDictionary(group => group.Key, group => new Queue<int>(group.Select(entry => entry.index)));
        var added = 0;
        foreach (var listen in incoming)
        {
            if (listen is null || listen.ItemId == Guid.Empty || !Enum.IsDefined(listen.Kind) || listen.Duration < TimeSpan.Zero)
                throw new JsonException("This file contains an invalid listening event.");

            // Match each existing copy once. Multiple real listens can have
            // identical timestamps, particularly when pulled from an iPod.
            if (existing.TryGetValue(EventKey(listen), out var matches) && matches.TryDequeue(out var index))
            {
                if (!merged[index].HasSnapshot && listen.HasSnapshot) merged[index] = listen;
                continue;
            }
            merged.Add(listen);
            added++;
        }

        WriteTransfer(Path_, merged);
        return (merged, added);
    }

    private static (ListenKind Kind, Guid Item, DateTime Occurred, TimeSpan Duration, bool Completed) EventKey(ListenEvent listen) =>
        (listen.Kind, listen.ItemId, listen.Occurred, listen.Duration, listen.Completed);

    private static void WriteTransfer(string path, IReadOnlyList<ListenEvent> events)
    {
        // Reject an invalid total before replacing any saved history.
        try { _ = events.Sum(listen => listen.Duration.Ticks); }
        catch (OverflowException) { throw new JsonException("The listening time in this file is too large."); }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(events, Options));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
