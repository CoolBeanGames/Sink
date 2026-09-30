using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace Sink.Services;

/// <summary>
/// Extracts embedded cover art from audio files into %AppData%/Sink/artwork so
/// the UI can bind to a plain file path. Files are keyed by album+artist, so a
/// whole album reuses one image.
/// </summary>
public static class Artwork
{
    public static string Directory { get; } = Path.Combine(LibraryStore.Directory, "artwork");

    /// <summary>Every imported cover is normalised to this square size, centre-cropped.</summary>
    public const int Size = 300;

    /// <summary>The cached file path for a key if it's already been extracted/saved, without doing any extraction work — for a hot path that shouldn't pay for a fresh TagLib read of every track (task 132).</summary>
    public static string? CachedPath(string key)
    {
        var path = Path.Combine(Directory, Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(key)))[..16] + ".jpg");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Saves the picture bytes for an album key and returns the file path, or null.</summary>
    public static string? Save(string key, byte[]? data, string? mimeType)
    {
        if (data is null || data.Length == 0) return null;
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            // All cover art, regardless of source, is centre-cropped to a square
            // and resized to Size x Size so the UI and the iPod get uniform tiles.
            var name = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(key)))[..16] + ".jpg";
            var path = Path.Combine(Directory, name);
            if (!File.Exists(path)) SquareCropTo(data, path);
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ImageFormatException or InvalidImageContentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Re-runs the square centre-crop on an existing artwork file in place (for
    /// art that was imported before cropping, or that just looks wrong). Returns
    /// true on success.
    /// </summary>
    public static bool Recrop(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var data = File.ReadAllBytes(path);
            SquareCropTo(data, path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ImageFormatException or InvalidImageContentException)
        {
            return false;
        }
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>
    /// Downloads a remote image (e.g. podcast show art) into the artwork cache,
    /// square-cropped like every other cover, and returns the local file path.
    /// Cached by URL, so repeat calls are cheap. Blocking — call from a worker.
    /// </summary>
    public static string? CacheRemote(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            return null;
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var name = "rmt_" + Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(url)))[..16] + ".jpg";
            var path = Path.Combine(Directory, name);
            if (File.Exists(path)) return path;
            var data = Http.GetByteArrayAsync(uri).GetAwaiter().GetResult();
            SquareCropTo(data, path);
            return path;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// Finds an album's real cover by album+artist name and caches it under
    /// the given key. Tries Deezer first (it carries far more of the catalog
    /// Sink downloads from — most of bbno$'s singles aren't on iTunes at all),
    /// then the iTunes Search API. Both covers are already square. Blocking —
    /// call from a worker thread. Returns null when nothing confidently
    /// matched or the requests failed; never throws.
    /// </summary>
    public static string? SearchAndDownloadAlbumArt(string album, string artist, string key)
    {
        if (string.IsNullOrWhiteSpace(album)) return null;
        var url = FindCoverUrl(album, artist, "https://api.deezer.com/search/album?limit=10&q=",
                      "data", r => Prop(r, "title"), r => r.TryGetProperty("artist", out var a) ? Prop(a, "name") : null,
                      r => Prop(r, "cover_xl"))
                  ?? FindCoverUrl(album, artist, "https://itunes.apple.com/search?entity=album&limit=10&term=",
                      "results", r => Prop(r, "collectionName"), r => Prop(r, "artistName"),
                      r => Prop(r, "artworkUrl100")?.Replace("100x100bb", "1200x1200bb"), itunes: true);
        if (url is null) return null;
        try { return SaveOverride(key, Http.GetByteArrayAsync(url).GetAwaiter().GetResult()); }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
    }

    private static readonly object ItunesPace = new();
    private static DateTime _lastItunesCall = DateTime.MinValue;

    /// <summary>
    /// One catalog lookup. A result counts when it's by the same artist, or —
    /// for collaborations another artist is credited first on — when its
    /// title is an exact, distinctive (10+ letters) match; a short title like
    /// "two" must not grab a stranger's cover. Exact titles win over partial.
    /// </summary>
    private static string? FindCoverUrl(string album, string artist, string searchPrefix, string listName,
        Func<JsonElement, string?> title, Func<JsonElement, string?> artistOf, Func<JsonElement, string?> cover, bool itunes = false)
    {
        try
        {
            if (itunes)
            {
                // Apple rate-limits the Search API to about 20 calls a minute;
                // a bulk cover repair would otherwise get blocked partway.
                lock (ItunesPace)
                {
                    var wait = _lastItunesCall.AddSeconds(3.1) - DateTime.UtcNow;
                    if (wait > TimeSpan.Zero) Thread.Sleep(wait);
                    _lastItunesCall = DateTime.UtcNow;
                }
            }
            var json = Http.GetStringAsync(searchPrefix + Uri.EscapeDataString($"{album} {artist}".Trim())).GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(listName, out var list) || list.ValueKind != JsonValueKind.Array) return null;
            var wanted = Normalize(album);
            var results = list.EnumerateArray().ToList();
            var byArtist = results.Where(r => string.IsNullOrWhiteSpace(artist) || SameArtist(artistOf(r), artist)).ToList();
            var pick = byArtist.FirstOrDefault(r => Normalize(title(r)) == wanted);
            if (pick.ValueKind == JsonValueKind.Undefined && wanted.Length >= 10)
                pick = results.FirstOrDefault(r => Normalize(title(r)) == wanted);
            if (pick.ValueKind == JsonValueKind.Undefined && byArtist.Count > 0) pick = byArtist[0];
            if (pick.ValueKind == JsonValueKind.Undefined) return null;
            var url = cover(pick);
            return string.IsNullOrWhiteSpace(url) ? null : url;
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
    }

    private static string? Prop(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool SameArtist(string? found, string wanted)
    {
        var a = Normalize(found);
        var b = Normalize(wanted);
        return a.Length > 0 && b.Length > 0 && (a == b || a.Contains(b) || b.Contains(a));
    }

    /// <summary>Lower-cased letters and digits only, so "bbno$" / "BBNO$" / "bbno" and "Mr. Miyagi" / "Mr.Miyagi" compare equal.</summary>
    private static string Normalize(string? value) =>
        new string((value ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>
    /// Force-saves image bytes under a key, overwriting any existing cached
    /// file at that key — unlike <see cref="Save"/>'s write-once cache
    /// semantics, this is for a deliberate one-off override (a downloaded
    /// album cover, or a manually chosen genre/artist image) that should
    /// take effect even if something was already cached there.
    /// </summary>
    public static string? SaveOverride(string key, byte[] data)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var name = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(key)))[..16] + ".jpg";
            var path = Path.Combine(Directory, name);
            SquareCropTo(data, path);
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ImageFormatException or InvalidImageContentException)
        {
            return null;
        }
    }

    /// <summary>Square-crops an image file to a <see cref="Size"/> JPEG and returns the bytes, or null.</summary>
    public static byte[]? SquareCropBytes(string imagePath)
    {
        try
        {
            if (!File.Exists(imagePath)) return null;
            using var image = Image.Load(File.ReadAllBytes(imagePath));
            var scale = (double)Size / Math.Min(image.Width, image.Height);
            var w = Math.Max(Size, (int)Math.Ceiling(image.Width * scale));
            var h = Math.Max(Size, (int)Math.Ceiling(image.Height * scale));
            image.Mutate(ctx =>
            {
                ctx.Resize(w, h);
                ctx.Crop(new Rectangle((w - Size) / 2, (h - Size) / 2, Size, Size));
            });
            using var ms = new MemoryStream();
            image.SaveAsJpeg(ms);
            return ms.ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ImageFormatException or InvalidImageContentException)
        {
            return null;
        }
    }

    /// <summary>Extracts embedded art from an audio file, force-cropping (overwrites any cached copy).</summary>
    public static string? ExtractAndCrop(string audioFilePath, string key)
    {
        try
        {
            using var tag = TagLib.File.Create(audioFilePath);
            var picture = tag.Tag.Pictures?.FirstOrDefault(p => p.Data?.Data?.Length > 0);
            if (picture is null) return null;
            System.IO.Directory.CreateDirectory(Directory);
            var name = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(key)))[..16] + ".jpg";
            var path = Path.Combine(Directory, name);
            SquareCropTo(picture.Data.Data, path);
            return path;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// Scales the image so its shorter side is <see cref="Size"/>, then crops the
    /// centred <see cref="Size"/> x <see cref="Size"/> square and writes it as JPEG.
    /// Explicit two-step so the output is always exactly square whatever the source.
    /// </summary>
    private static void SquareCropTo(byte[] data, string path)
    {
        using var image = Image.Load(data);
        var scale = (double)Size / Math.Min(image.Width, image.Height);
        var scaledW = Math.Max(Size, (int)Math.Ceiling(image.Width * scale));
        var scaledH = Math.Max(Size, (int)Math.Ceiling(image.Height * scale));
        image.Mutate(ctx =>
        {
            ctx.Resize(scaledW, scaledH);
            ctx.Crop(new Rectangle((scaledW - Size) / 2, (scaledH - Size) / 2, Size, Size));
        });
        image.SaveAsJpeg(path);
    }
}
