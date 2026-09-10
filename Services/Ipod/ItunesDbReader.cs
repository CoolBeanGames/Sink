using System.IO;
using System.Text;

namespace Sink.Services.Ipod;

/// <summary>
/// Reads an iPod's <c>iPod_Control/iTunes/iTunesDB</c> — the binary, chunked
/// database the iPod firmware itself uses. This is what lets Sink see and edit
/// the music that is actually on the device.
///
/// The format is a tree of 4-byte-tagged chunks (mhbd → mhsd → mhlt/mhlp →
/// mhit/mhyp → mhod/mhip), little-endian throughout. This reader is tolerant:
/// unknown chunks and fields are skipped rather than treated as errors.
///
/// Only plain <c>iTunesDB</c> is supported (iPod classic / video / nano 1–3G).
/// iPod touch / iPhone use a SQLite library instead.
/// </summary>
public static class ItunesDbReader
{
    private static readonly DateTime MacEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static bool DatabaseExists(string ipodRoot) => File.Exists(DbPath(ipodRoot));

    public static string DbPath(string ipodRoot) =>
        Path.Combine(ipodRoot, "iPod_Control", "iTunes", "iTunesDB");

    public static IpodLibrary Read(string ipodRoot)
    {
        var library = new IpodLibrary();
        var bytes = File.ReadAllBytes(DbPath(ipodRoot));
        if (bytes.Length < 4 || Tag(bytes, 0) != "mhbd")
            throw new InvalidDataException("Not a plain iTunesDB (mhbd header missing).");

        var mhbdHeaderLen = U32(bytes, 4);
        var childCount = U32(bytes, 0x14);
        var pos = (int)mhbdHeaderLen;

        for (var i = 0; i < childCount && pos + 16 <= bytes.Length; i++)
        {
            if (Tag(bytes, pos) != "mhsd") break;
            var headerLen = U32(bytes, pos + 4);
            var totalLen = U32(bytes, pos + 8);
            var type = U32(bytes, pos + 0x0C);
            var childPos = pos + (int)headerLen;

            if (type == 1) ReadTrackList(bytes, childPos, library);
            else if (type is 2 or 3) ReadPlaylistList(bytes, childPos, library);

            pos += (int)Math.Max(totalLen, headerLen);
        }

        LoadDeviceInfo(ipodRoot, library);
        library.DeviceName ??= library.Playlists.FirstOrDefault(p => p.IsMaster)?.Name is { Length: > 0 } n && n != "iPod"
            ? n
            : null;
        return library;
    }

    private static void ReadTrackList(byte[] b, int pos, IpodLibrary lib)
    {
        if (pos + 12 > b.Length || Tag(b, pos) != "mhlt") return;
        var headerLen = U32(b, pos + 4);
        var count = U32(b, pos + 8);
        var p = pos + (int)headerLen;

        for (var i = 0; i < count && p + 16 <= b.Length; i++)
        {
            if (Tag(b, p) != "mhit") break;
            var mhitHeaderLen = U32(b, p + 4);
            var mhitTotalLen = U32(b, p + 8);
            var track = ReadTrack(b, p, (int)mhitHeaderLen);
            lib.Tracks.Add(track);
            p += (int)Math.Max(mhitTotalLen, mhitHeaderLen);
        }
    }

    private static IpodDbTrack ReadTrack(byte[] b, int p, int headerLen)
    {
        var t = new IpodDbTrack
        {
            TrackId = U32(b, p + 0x10),
            Rating = b[p + 0x1F] / 20,
            SizeBytes = U32(b, p + 0x24),
            Duration = TimeSpan.FromMilliseconds(U32(b, p + 0x28)),
            TrackNumber = (int)U32(b, p + 0x2C),
            TrackCount = (int)U32(b, p + 0x30),
            Year = (int)U32(b, p + 0x34),
            BitrateKbps = (int)U32(b, p + 0x38),
            PlayCount = (int)U32(b, p + 0x50),
            DiscNumber = (int)U32(b, p + 0x5C),
            FileType = FourCc(U32(b, p + 0x18)),
        };
        if (headerLen >= 0x78) t.DbId = U64(b, p + 0x70);

        var numMhods = U32(b, p + 0x0C);
        var mp = p + headerLen;
        for (var i = 0; i < numMhods && mp + 12 <= b.Length; i++)
        {
            if (Tag(b, mp) != "mhod") break;
            var mhodTotal = U32(b, mp + 8);
            var (type, value) = ReadStringMhod(b, mp);
            switch (type)
            {
                case 1: t.Title = value; break;
                case 2: t.IpodPath = value; break;
                case 3: t.Album = value; break;
                case 4: t.Artist = value; break;
                case 5: t.Genre = value; break;
                case 8: t.Comment = value; break;
                case 12: t.Composer = value; break;
            }
            mp += (int)Math.Max(mhodTotal, 12u);
        }
        if (string.IsNullOrEmpty(t.Title)) t.Title = "Unknown";
        return t;
    }

    private static void ReadPlaylistList(byte[] b, int pos, IpodLibrary lib)
    {
        if (pos + 12 > b.Length || Tag(b, pos) != "mhlp") return;
        var headerLen = U32(b, pos + 4);
        var count = U32(b, pos + 8);
        var p = pos + (int)headerLen;

        for (var i = 0; i < count && p + 20 <= b.Length; i++)
        {
            if (Tag(b, p) != "mhyp") break;
            var mhypHeaderLen = U32(b, p + 4);
            var mhypTotalLen = U32(b, p + 8);
            lib.Playlists.Add(ReadPlaylist(b, p, (int)mhypHeaderLen));
            p += (int)Math.Max(mhypTotalLen, mhypHeaderLen);
        }
    }

    private static IpodDbPlaylist ReadPlaylist(byte[] b, int p, int headerLen)
    {
        var pl = new IpodDbPlaylist
        {
            IsMaster = U32(b, p + 0x14) != 0,
        };
        if (headerLen >= 0x28) pl.PlaylistId = U64(b, p + 0x20);

        var numMhods = U32(b, p + 0x0C);
        var numItems = U32(b, p + 0x10);
        var mp = p + headerLen;

        for (var i = 0; i < numMhods && mp + 12 <= b.Length; i++)
        {
            if (Tag(b, mp) != "mhod") break;
            var mhodTotal = U32(b, mp + 8);
            var (type, value) = ReadStringMhod(b, mp);
            if (type == 1) pl.Name = value;
            mp += (int)Math.Max(mhodTotal, 12u);
        }

        for (var i = 0; i < numItems && mp + 20 <= b.Length; i++)
        {
            if (Tag(b, mp) != "mhip") break;
            var mhipHeaderLen = U32(b, mp + 4);
            var mhipTotalLen = U32(b, mp + 8);
            var mhipMhods = U32(b, mp + 0x0C);
            pl.TrackIds.Add(U32(b, mp + 0x18));
            var advance = (int)Math.Max(mhipTotalLen, mhipHeaderLen);
            // mhipTotalLen already covers trailing mhods; if it doesn't, skip them.
            if (mhipTotalLen <= mhipHeaderLen && mhipMhods > 0)
            {
                var q = mp + (int)mhipHeaderLen;
                for (var m = 0; m < mhipMhods && q + 12 <= b.Length; m++)
                {
                    q += (int)Math.Max(U32(b, q + 8), 12u);
                }
                advance = q - mp;
            }
            mp += advance;
        }

        if (string.IsNullOrEmpty(pl.Name)) pl.Name = pl.IsMaster ? "iPod" : "Playlist";
        return pl;
    }

    /// <summary>Reads a string data object. Returns (type, decoded string).</summary>
    private static (uint type, string value) ReadStringMhod(byte[] b, int p)
    {
        var headerLen = U32(b, p + 4);
        var totalLen = U32(b, p + 8);
        var type = U32(b, p + 0x0C);

        // UTF-8 payload objects (podcast URLs) sit straight after the header.
        if (type is 15 or 16)
        {
            var start = p + (int)headerLen;
            var len = (int)totalLen - (int)headerLen;
            return len > 0 && start + len <= b.Length
                ? (type, Encoding.UTF8.GetString(b, start, len))
                : (type, "");
        }

        // Standard string object: a 16-byte sub-header then UTF-16LE data.
        var sub = p + (int)headerLen;
        if (sub + 16 > b.Length) return (type, "");
        var byteLen = (int)U32(b, sub + 4);
        var strStart = sub + 16;
        if (byteLen <= 0 || strStart + byteLen > b.Length) return (type, "");
        return (type, Encoding.Unicode.GetString(b, strStart, byteLen));
    }

    private static void LoadDeviceInfo(string ipodRoot, IpodLibrary lib)
    {
        try
        {
            var info = SysInfoReader.Read(ipodRoot);
            if (info is null) return;
            lib.ModelNumber = info.ModelNumber;
            lib.SerialNumber = info.SerialNumber;
            lib.FirmwareVersion = info.FirmwareVersion;
            lib.CapacityBytes = info.CapacityBytes;
        }
        catch { /* device info is best-effort */ }
    }

    private static string Tag(byte[] b, int off) =>
        off + 4 <= b.Length ? Encoding.ASCII.GetString(b, off, 4) : "";

    private static uint U32(byte[] b, int off) =>
        off + 4 <= b.Length ? BitConverter.ToUInt32(b, off) : 0;

    private static ulong U64(byte[] b, int off) =>
        off + 8 <= b.Length ? BitConverter.ToUInt64(b, off) : 0;

    private static string FourCc(uint value)
    {
        if (value == 0) return "";
        Span<char> c =
        [
            (char)((value >> 24) & 0xFF), (char)((value >> 16) & 0xFF),
            (char)((value >> 8) & 0xFF), (char)(value & 0xFF)
        ];
        return new string(c).Trim('\0', ' ');
    }
}
