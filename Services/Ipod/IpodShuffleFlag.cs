using System.Reflection;

namespace Sink.Services.Ipod;

/// <summary>
/// Reads and writes the on-device "skip when shuffling" bit on a Clickwheel
/// iTunesDB track. Clickwheel parses and serialises the field (<c>_skipWhenShuffling</c>)
/// but doesn't surface a public accessor, so we reach it by reflection and
/// fail soft — same trick as <see cref="IpodBookmarks"/>.
/// </summary>
internal static class IpodShuffleFlag
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly string[] Names = ["_skipWhenShuffling", "SkipWhenShuffling"];

    public static bool Get(object track)
    {
        try
        {
            var type = track.GetType();
            foreach (var name in Names)
            {
                var member = (MemberInfo?)type.GetProperty(name, Flags) ?? type.GetField(name, Flags);
                var raw = member switch
                {
                    PropertyInfo p => p.GetValue(track),
                    FieldInfo f => f.GetValue(track),
                    _ => null,
                };
                if (raw is bool b) return b;
            }
        }
        catch { }
        return false;
    }

    /// <summary>Sets the flag; returns true only if the on-device value actually changed (so callers know whether to save).</summary>
    public static bool Set(object track, bool skipWhenShuffling)
    {
        try
        {
            if (Get(track) == skipWhenShuffling) return false;
            var type = track.GetType();
            foreach (var name in Names)
            {
                var prop = type.GetProperty(name, Flags);
                if (prop is { CanWrite: true }) { prop.SetValue(track, skipWhenShuffling); MarkDirty(track); return true; }
                var field = type.GetField(name, Flags);
                if (field is not null) { field.SetValue(track, skipWhenShuffling); MarkDirty(track); return true; }
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Every public Track property setter also flips a private <c>_isDirty</c>
    /// field, which is what MusicDatabase.IsDirty (and so IPod.SaveChanges)
    /// actually checks before writing anything at all — going straight through
    /// reflection for a field with no public setter skips that, so
    /// Clickwheel silently no-ops the whole save. Set it ourselves to match.
    /// </summary>
    private static void MarkDirty(object track)
    {
        try { track.GetType().GetField("_isDirty", Flags)?.SetValue(track, true); }
        catch { }
    }
}
