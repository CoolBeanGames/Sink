using System.Text.Json.Serialization;

namespace Sink.Models;

public sealed class Track : ITitleTrimmable
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Title { get; set; }
    public required string Artist { get; set; }
    public required string Album { get; set; }
    public required string Genre { get; set; }
    public required string FileName { get; set; }
    public string? FilePath { get; set; }

    /// <summary>
    /// The real-FLAC original kept in the high-quality folder when the
    /// library copy is in another format ("Keep high quality"). Null when the
    /// library file itself is the high-quality copy, or none was kept.
    /// </summary>
    public string? OriginalPath { get; set; }
    public int TrackNumber { get; set; }
    public int Year { get; set; }
    public TimeSpan Duration { get; init; }
    public bool ExcludedFromShuffle { get; set; }

    /// <summary>
    /// This track's Title+Artist+Album+TrackNumber identity (see
    /// <see cref="Sink.Services.Ipod.IpodDbTrack.MakeKey"/>) as of the last
    /// successful iPod sync. A later metadata edit changes that identity,
    /// so it's kept here to find the existing on-device copy by its old
    /// identity and update it in place, instead of the sync silently
    /// treating it as a new track and copying the file again as a
    /// duplicate.
    /// </summary>
    public string? LastSyncedKey { get; set; }

    /// <summary>Heart-icon favorite (task 127 — Reflect).</summary>
    public bool IsFavorite { get; set; }

    /// <summary>
    /// This track's PlayCount on each iPod (keyed by device serial) as of the
    /// last sync — the baseline the next sync diffs against so device plays
    /// aren't double-counted into Reflect (task 128).
    /// </summary>
    public Dictionary<string, int>? SyncedIpodPlayCounts { get; set; }

    /// <summary>Absolute path to extracted cover art (jpg/png) under %AppData%/Sink/artwork, if any.</summary>
    public string? ArtworkPath { get; set; }

    [JsonIgnore]
    public string DurationText => Duration.ToString(@"m\:ss");

    /// <summary>
    /// Set by the startup scan when this track's file can't be found (drive
    /// offline, file moved or deleted). Not persisted: every startup checks
    /// again, so it clears itself once the file is back.
    /// </summary>
    [JsonIgnore]
    public bool IsMissing { get; set; }

    /// <summary>Result of the last "Check quality" / "Download high quality" analysis (an <c>AudioQualityVerdict</c> name) of <see cref="QualityCheckedPath"/>.</summary>
    public string? QualityVerdict { get; set; }
    public string? QualityCheckedPath { get; set; }

    /// <summary>
    /// What playback will actually open — the library file's format, plus the
    /// analysed quality when it's been checked ("FLAC · fake" is an MP3 in a
    /// FLAC wrapper) and whether a separate high-quality original is kept.
    /// </summary>
    [JsonIgnore]
    public string FormatText
    {
        get
        {
            var ext = System.IO.Path.GetExtension(FilePath ?? "").TrimStart('.').ToUpperInvariant();
            if (ext.Length == 0) ext = "?";
            var checkedThisFile = QualityVerdict is not null
                && string.Equals(QualityCheckedPath, FilePath, StringComparison.OrdinalIgnoreCase);
            var quality = !checkedThisFile ? "" : QualityVerdict switch
            {
                "Lossless" => " · lossless",
                "FakeLossless" => " · fake",
                _ => "",
            };
            return ext + quality + (OriginalPath is null ? "" : " · HQ kept");
        }
    }

    [JsonIgnore]
    public string FileTooltip =>
        (FilePath ?? "(no file)") + (OriginalPath is null ? "" : $"\nHigh-quality original: {OriginalPath}");

    /// <summary>
    /// Total tracked listens (in-app + folded-in iPod plays) — recomputed by
    /// MainWindow.RecomputePlayCounts from ReflectStore's ListenEvents, not
    /// persisted itself. Surfaced directly on the Songs list so play-count
    /// tracking is visibly verifiable rather than only inferable from Reflect
    /// (task: "play count").
    /// </summary>
    [JsonIgnore]
    public int PlayCount { get; set; }

    /// <summary>
    /// True when this track's current Title+Artist+Album+TrackNumber
    /// identity doesn't match what was last pushed to the iPod — either it
    /// was never synced at all (LastSyncedKey is still null) or it's been
    /// retagged since its last sync. Drives the unsynced-changes dot.
    /// </summary>
    [JsonIgnore]
    public bool HasUnsyncedChanges =>
        LastSyncedKey != Sink.Services.Ipod.IpodDbTrack.MakeKey(Title, Artist, Album, TrackNumber);
}
