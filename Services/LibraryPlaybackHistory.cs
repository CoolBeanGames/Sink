using System.IO;
using System.Text.Json;

namespace Sink.Services;

/// <summary>A recent music playback and its last saved position.</summary>
public sealed class LibraryPlaybackEntry
{
    public Guid TrackId { get; set; }
    public DateTime LastStartedUtc { get; set; }
    public double PositionSeconds { get; set; }
    public double DurationSeconds { get; set; }
    public bool Completed { get; set; }
    public bool ListenRecorded { get; set; }
}

/// <summary>
/// Remembers recently started library tracks independently of Reflect's
/// counted listens. A short play can therefore still be resumed without
/// changing the meaning of a listen or the track's play count.
/// </summary>
public sealed class LibraryPlaybackHistory
{
    private const int MaxEntries = 30;
    private static readonly string HistoryPath = Path.Combine(LibraryStore.Directory, "library-playback-history.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly List<LibraryPlaybackEntry> _entries;

    private LibraryPlaybackHistory(List<LibraryPlaybackEntry> entries) => _entries = entries;

    public IReadOnlyList<LibraryPlaybackEntry> Entries => _entries;

    public static LibraryPlaybackHistory Load()
    {
        try
        {
            if (!File.Exists(HistoryPath)) return new LibraryPlaybackHistory([]);
            var entries = JsonSerializer.Deserialize<List<LibraryPlaybackEntry>>(File.ReadAllText(HistoryPath), Options) ?? [];
            // Be tolerant of old, incomplete, and duplicate entries.
            return new LibraryPlaybackHistory(entries
                .Where(entry => entry is not null && entry.TrackId != Guid.Empty)
                .OrderByDescending(entry => entry.LastStartedUtc)
                .DistinctBy(entry => entry.TrackId)
                .Take(MaxEntries)
                .ToList());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new LibraryPlaybackHistory([]);
        }
    }

    /// <summary>Moves a started or resumed track to the top of history.</summary>
    public void Start(Guid trackId, TimeSpan position, TimeSpan duration, bool listenRecorded = false)
    {
        if (trackId == Guid.Empty) return;
        _entries.RemoveAll(entry => entry.TrackId == trackId);
        var entry = new LibraryPlaybackEntry { TrackId = trackId, LastStartedUtc = DateTime.UtcNow, ListenRecorded = listenRecorded };
        SetPosition(entry, position, duration, completed: false);
        _entries.Insert(0, entry);
        if (_entries.Count > MaxEntries) _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
        Save();
    }

    /// <summary>Saves a position without changing when the track was started.</summary>
    public void Update(Guid trackId, TimeSpan position, TimeSpan duration, bool completed = false)
    {
        var entry = _entries.FirstOrDefault(item => item.TrackId == trackId);
        if (entry is null) return;
        SetPosition(entry, position, duration, completed);
        Save();
    }

    public void MarkListenRecorded(Guid trackId)
    {
        var entry = _entries.FirstOrDefault(item => item.TrackId == trackId);
        if (entry is null || entry.ListenRecorded) return;
        entry.ListenRecorded = true;
        Save();
    }

    private static void SetPosition(LibraryPlaybackEntry entry, TimeSpan position, TimeSpan duration, bool completed)
    {
        var seconds = Math.Max(0, duration.TotalSeconds);
        entry.DurationSeconds = seconds;
        entry.PositionSeconds = completed ? 0 : Math.Clamp(position.TotalSeconds, 0, seconds > 0 ? seconds : double.MaxValue);
        entry.Completed = completed;
    }

    private void Save()
    {
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(LibraryStore.Directory);
            temporary = HistoryPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_entries, Options));
            File.Move(temporary, HistoryPath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Log.Warn($"Could not save library playback history: {e.Message}");
        }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
