using System.Reflection;

namespace Sink.Services.Ipod;

/// <summary>
/// Reads and writes the per-track resume position ("bookmark_time") on a
/// Clickwheel iTunesDB track. Clickwheel parses and serialises the field but
/// doesn't surface a public accessor, so we reach it by reflection and fail
/// soft — callers fall back to play-count-only sync when it isn't there.
/// </summary>
internal static class IpodBookmarks
{
    private const BindingFlags Flags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // milliseconds, then legacy "position in seconds" as a fallback
    private static readonly string[] MsNames = ["_bookmarkTime", "BookmarkTime", "bookmarkTime"];
    private static readonly string[] SecNames = ["_bookmarkPosition", "BookmarkPosition", "bookmarkPosition"];
    private static readonly string[] RememberNames = ["RememberPlaybackPosition", "_rememberPlaybackPosition"];

    public static long GetMs(object track)
    {
        try
        {
            if (TryRead(track, MsNames, out var ms) && ms > 0) return ms;
            if (TryRead(track, SecNames, out var sec) && sec > 0) return sec * 1000;
        }
        catch { }
        return 0;
    }

    public static void SetMs(object track, long milliseconds)
    {
        try
        {
            TryWrite(track, MsNames, milliseconds);
            TryWrite(track, SecNames, milliseconds / 1000);
            SetRemember(track, milliseconds > 0);
        }
        catch { }
    }

    private static void SetRemember(object track, bool value)
    {
        var type = track.GetType();
        foreach (var name in RememberNames)
        {
            var prop = type.GetProperty(name, Flags);
            if (prop is { CanWrite: true }) { prop.SetValue(track, value); return; }
            var field = type.GetField(name, Flags);
            if (field is not null) { field.SetValue(track, value); return; }
        }
    }

    private static bool TryRead(object track, string[] names, out long value)
    {
        var type = track.GetType();
        foreach (var name in names)
        {
            var member = (MemberInfo?)type.GetProperty(name, Flags) ?? type.GetField(name, Flags);
            var raw = member switch
            {
                PropertyInfo p => p.GetValue(track),
                FieldInfo f => f.GetValue(track),
                _ => null,
            };
            if (raw is null) continue;
            value = Convert.ToInt64(raw);
            return true;
        }
        value = 0;
        return false;
    }

    private static void TryWrite(object track, string[] names, long value)
    {
        var type = track.GetType();
        foreach (var name in names)
        {
            var prop = type.GetProperty(name, Flags);
            if (prop is { CanWrite: true })
            {
                prop.SetValue(track, ConvertTo(prop.PropertyType, value));
                return;
            }
            var field = type.GetField(name, Flags);
            if (field is not null)
            {
                field.SetValue(track, ConvertTo(field.FieldType, value));
                return;
            }
        }
    }

    private static object ConvertTo(Type target, long value)
    {
        var t = Nullable.GetUnderlyingType(target) ?? target;
        return t == typeof(uint) ? (uint)Math.Clamp(value, 0, uint.MaxValue)
             : t == typeof(int) ? (int)Math.Clamp(value, int.MinValue, int.MaxValue)
             : t == typeof(long) ? value
             : Convert.ChangeType(value, t);
    }
}
