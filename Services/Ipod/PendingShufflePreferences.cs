using System.IO;
using Sink.Models;

namespace Sink.Services.Ipod;

/// <summary>Coalesces rapid edits without letting completion of an older write discard a newer preference.</summary>
internal sealed class PendingShufflePreferences
{
    internal sealed record Update(string Key, string Root, Track Track, bool Excluded, bool MatchDevicePaths, long Revision);
    private readonly Dictionary<string, Update> _updates = new(StringComparer.OrdinalIgnoreCase);
    private long _revision;
    public int Count => _updates.Count;
    public IEnumerable<Update> Values => _updates.Values;

    public void Enqueue(string root, IReadOnlyList<Track> tracks, bool excluded, bool matchDevicePaths)
    {
        foreach (var track in tracks)
        {
            var identity = matchDevicePaths && !string.IsNullOrWhiteSpace(track.FilePath)
                ? "path:" + Path.GetFullPath(track.FilePath)
                : "key:" + (track.LastSyncedKey ?? IpodDbTrack.MakeKey(track.Title, track.Artist, track.Album, track.TrackNumber));
            var key = root + "|" + identity;
            var snapshot = new Track { Id = track.Id, Title = track.Title, Artist = track.Artist, Album = track.Album,
                Genre = track.Genre, FileName = track.FileName, FilePath = track.FilePath, TrackNumber = track.TrackNumber,
                LastSyncedKey = track.LastSyncedKey, ExcludedFromShuffle = excluded };
            _updates[key] = new Update(key, root, snapshot, excluded, matchDevicePaths, ++_revision);
        }
    }

    public void Acknowledge(Update saved)
    {
        if (_updates.TryGetValue(saved.Key, out var current) && current.Revision == saved.Revision)
            _updates.Remove(saved.Key);
    }
}
