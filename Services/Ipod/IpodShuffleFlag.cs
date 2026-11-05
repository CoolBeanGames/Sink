using System.Reflection;
using CwTrack = Clickwheel.Parsers.iTunesDB.Track;

namespace Sink.Services.Ipod;

/// <summary>
/// Reads and writes the on-device "skip when shuffling" bit on a Clickwheel
/// iTunesDB track. Clickwheel parses and serialises the field (<c>_skipWhenShuffling</c>)
/// but doesn't surface a public accessor, so we reach it by reflection and
/// mark the track dirty so Clickwheel persists flag-only edits. A changed
/// dependency must produce an error instead of silently ignoring the setting.
/// </summary>
internal static class IpodShuffleFlag
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly FieldInfo SkipField = RequiredField("_skipWhenShuffling");
    private static readonly FieldInfo DirtyField = RequiredField("_isDirty");

    public static bool Get(CwTrack track) => (bool)SkipField.GetValue(track)!;

    /// <summary>Sets the flag; returns true only if the on-device value actually changed (so callers know whether to save).</summary>
    public static bool Set(CwTrack track, bool skipWhenShuffling)
    {
        if (Get(track) == skipWhenShuffling) return false;
        SkipField.SetValue(track, skipWhenShuffling);
        MarkDirty(track);
        return true;
    }

    /// <summary>
    /// Every public Track property setter also flips a private <c>_isDirty</c>
    /// field, which is what MusicDatabase.IsDirty (and so IPod.SaveChanges)
    /// actually checks before writing anything at all — going straight through
    /// reflection for a field with no public setter skips that, so
    /// Clickwheel silently no-ops the whole save. Set it ourselves to match.
    /// </summary>
    internal static void MarkDirty(CwTrack track) => DirtyField.SetValue(track, true);

    private static FieldInfo RequiredField(string name) => typeof(CwTrack).GetField(name, Flags)
        ?? throw new NotSupportedException($"This version of Clickwheel cannot update the iPod field {name}.");
}
